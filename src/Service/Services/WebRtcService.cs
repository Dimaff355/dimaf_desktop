using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Config;
using RemoteDesktop.Shared.Messaging;
using SIPSorcery.Net;
using SIPSorcery.SDP;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Abstractions.V1;
using SIPSorceryMedia.Encoders;

namespace RemoteDesktop.Service.Services;

public sealed class WebRtcService : IAsyncDisposable
{
    private readonly ILogger<WebRtcService> _logger;
    private RTCPeerConnection? _peerConnection;
    private RTCDataChannel? _controlChannel;
    private RTCDataChannel? _frameChannel;
    private MediaStreamTrack? _videoTrack;
    private VpxEncoder? _vp8Encoder;
    private int _encoderWidth;
    private int _encoderHeight;
    private uint _videoTimestamp;
    private ushort _videoSequenceNumber;
    private ImmutableArray<string> _stunServers = ImmutableArray<string>.Empty;
    private readonly List<SDPMediaFormat> _videoFormats = new()
    {
        new SDPMediaFormat((int)SDPWellKnownMediaFormatsEnum.VP8, SDPMediaFormatsEnum.VP8)
    };
    private const int VideoClockRate = 90000;

    public event Func<SdpOffer, Task>? OfferReady;
    public event Func<IceCandidate, Task>? IceCandidateReady;
    public event Func<string, Task>? IceStateChanged;
    public event Func<Task>? ControlChannelOpened;
    public event Func<Task>? ControlChannelClosed;
    public event Func<string, Task>? ControlMessageReceived;
    public event Func<Task>? FrameChannelOpened;
    public event Func<Task>? FrameChannelClosed;

    public bool HasVideoTrack => _videoTrack is not null;

    public WebRtcService(ILogger<WebRtcService> logger)
    {
        _logger = logger;
    }

    public async Task StartOfferAsync(IEnumerable<string> stunServers, TurnConfig? turn, CancellationToken cancellationToken)
    {
        _stunServers = stunServers.ToImmutableArray();
        await ResetAsync().ConfigureAwait(false);

        var config = new RTCConfiguration
        {
            iceServers = BuildIceServers(turn)
        };

        _peerConnection = new RTCPeerConnection(config);
        _peerConnection.onicecandidate += HandleLocalIceCandidate;
        _peerConnection.onconnectionstatechange += state => HandleStateChanged(state.ToString());

        _peerConnection.ondatachannel += channel => SetupDataChannel(channel);

        AttachVideoTrack();

        SetupDataChannel(_peerConnection.createDataChannel("control", null));
        SetupDataChannel(_peerConnection.createDataChannel("frames", null));

        var offer = _peerConnection.createOffer(null);
        await _peerConnection.setLocalDescription(offer).ConfigureAwait(false);

        if (OfferReady is not null)
        {
            await OfferReady.Invoke(new SdpOffer(offer.sdp, offer.type.ToString())).ConfigureAwait(false);
        }
    }

    public async Task AcceptAnswerAsync(SdpAnswer answer)
    {
        if (_peerConnection is null)
        {
            _logger.LogWarning("Ignoring SDP answer without an active peer connection");
            return;
        }

        var description = new RTCSessionDescriptionInit
        {
            sdp = answer.Sdp,
            type = RTCSdpType.answer
        };

        await _peerConnection.setRemoteDescription(description).ConfigureAwait(false);
    }

    public Task AddRemoteCandidateAsync(IceCandidate candidate)
    {
        if (_peerConnection is null)
        {
            _logger.LogWarning("Dropping ICE candidate because the peer connection is not ready");
            return Task.CompletedTask;
        }

        var ice = new RTCIceCandidateInit
        {
            candidate = candidate.Candidate,
            sdpMid = candidate.SdpMid,
            sdpMLineIndex = candidate.SdpMLineIndex
        };

        return _peerConnection.addIceCandidate(ice);
    }

    private void HandleLocalIceCandidate(RTCIceCandidate candidate)
    {
        if (candidate is null)
        {
            return;
        }

        _ = IceCandidateReady?.Invoke(new IceCandidate(candidate.toString(), candidate.sdpMid, candidate.sdpMLineIndex));
    }

    private void HandleStateChanged(string state)
    {
        _logger.LogInformation("WebRTC connection state changed to {State}", state);
        _ = IceStateChanged?.Invoke(state);
    }

    private void SetupDataChannel(RTCDataChannel channel)
    {
        if (channel is null)
        {
            return;
        }

        switch (channel.label)
        {
            case "control":
                SetupControlChannel(channel);
                break;
            case "frames":
                SetupFrameChannel(channel);
                break;
            default:
                _logger.LogDebug("Ignoring unexpected data channel {Label}", channel.label);
                break;
        }
    }

    private void SetupControlChannel(RTCDataChannel channel)
    {
        _controlChannel = channel;
        channel.onopen += () =>
        {
            _logger.LogInformation("Control data channel opened");
            _ = ControlChannelOpened?.Invoke();
        };

        channel.onclose += () =>
        {
            _logger.LogInformation("Control data channel closed");
            _ = ControlChannelClosed?.Invoke();
        };

        channel.onmessage += (_, proto, data) =>
        {
            if (data is null || data.Length == 0)
            {
                return;
            }

            var text = Encoding.UTF8.GetString(data);
            _ = ControlMessageReceived?.Invoke(text);
        };
    }

    private void SetupFrameChannel(RTCDataChannel channel)
    {
        _frameChannel = channel;
        channel.onopen += () =>
        {
            _logger.LogInformation("Frame data channel opened");
            _ = FrameChannelOpened?.Invoke();
        };

        channel.onclose += () =>
        {
            _logger.LogInformation("Frame data channel closed");
            _ = FrameChannelClosed?.Invoke();
        };
    }

    public async Task<bool> TrySendControlMessageAsync(IDataChannelMessage message, CancellationToken cancellationToken)
    {
        if (_controlChannel is null || _controlChannel.readyState != RTCDataChannelState.open)
        {
            return false;
        }

        var payload = System.Text.Json.JsonSerializer.Serialize(message, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });

        var buffer = Encoding.UTF8.GetBytes(payload);
        await _controlChannel.send(buffer).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TrySendFrameAsync(FrameBinaryHeader header, byte[] payload, CancellationToken cancellationToken)
    {
        if (_frameChannel is null || _frameChannel.readyState != RTCDataChannelState.open)
        {
            return false;
        }

        var headerBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(header, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });

        var buffer = new byte[headerBytes.Length + 1 + payload.Length];
        Buffer.BlockCopy(headerBytes, 0, buffer, 0, headerBytes.Length);
        buffer[headerBytes.Length] = 0; // delimiter between header and payload
        Buffer.BlockCopy(payload, 0, buffer, headerBytes.Length + 1, payload.Length);

        await _frameChannel.send(buffer).ConfigureAwait(false);
        return true;
    }

    public Task<bool> TrySendVideoFrameAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (_peerConnection is null || _videoTrack is null)
        {
            return Task.FromResult(false);
        }

        if (frame.BgraData.Length == 0)
        {
            return Task.FromResult(false);
        }

        if (_vp8Encoder is null || _encoderWidth != frame.Width || _encoderHeight != frame.Height)
        {
            _vp8Encoder?.Dispose();
            _vp8Encoder = new VpxEncoder();
            _vp8Encoder.InitEncoder((uint)frame.Width, (uint)frame.Height, 30);
            _encoderWidth = frame.Width;
            _encoderHeight = frame.Height;
            _videoTimestamp = 0;
            _videoSequenceNumber = 0;
        }

        var encoded = _vp8Encoder!.Encode(frame.BgraData, VideoPixelFormatsEnum.Bgra, (uint)frame.Width, (uint)frame.Height);
        if (encoded == null || encoded.Count == 0)
        {
            return Task.FromResult(false);
        }

        var payloadType = _videoFormats[0].FormatID;
        var ssrc = _videoTrack.Ssrc ?? (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        var sent = false;

        foreach (var fragment in encoded)
        {
            var packet = new RTPPacket(fragment.EncodedData.Length)
            {
                Header =
                {
                    PayloadType = payloadType,
                    SequenceNumber = _videoSequenceNumber++,
                    Timestamp = _videoTimestamp,
                    MarkerBit = fragment.IsKeyFrame,
                    SyncSource = ssrc
                }
            };

            Buffer.BlockCopy(fragment.EncodedData, 0, packet.Payload, 0, fragment.EncodedData.Length);

            _peerConnection.SendRtpRaw(SDPMediaTypesEnum.video, packet.GetBytes(), packet.Header.Timestamp, packet.Header.MarkerBit, packet.Header.PayloadType, packet.Header.SyncSource, packet.Header.SequenceNumber);
            sent = true;
        }

        _videoTimestamp += (uint)(VideoClockRate / 30);
        return Task.FromResult(sent);
    }

    private List<RTCIceServer> BuildIceServers(TurnConfig? turn)
    {
        var servers = _stunServers.Select(url => new RTCIceServer { urls = new List<string> { url } }).ToList();
        if (turn is not null && !string.IsNullOrWhiteSpace(turn.Url))
        {
            servers.Add(new RTCIceServer
            {
                urls = new List<string> { turn.Url },
                username = turn.Username,
                credential = turn.Credential,
                credentialType = RTCIceCredentialType.password
            });
        }

        return servers;
    }

    public Task ResetAsync()
    {
        if (_peerConnection is not null)
        {
            _peerConnection.onicecandidate -= HandleLocalIceCandidate;
            _peerConnection.close("reset");
            _peerConnection.Dispose();
            _peerConnection = null;
        }

        _controlChannel = null;
        _frameChannel = null;
        _videoTrack = null;
        _vp8Encoder?.Dispose();
        _vp8Encoder = null;
        _encoderWidth = 0;
        _encoderHeight = 0;

        return Task.CompletedTask;
    }

    private void AttachVideoTrack()
    {
        if (_peerConnection is null)
        {
            return;
        }

        _videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, mediaFormats: _videoFormats);
        _peerConnection.addTrack(_videoTrack);
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync().ConfigureAwait(false);
    }
}
