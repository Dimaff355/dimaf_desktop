using System.Collections.Generic;
using RemoteDesktop.Shared.Messaging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions.V1;

namespace OperatorConsole;

internal sealed class WebRtcOperator : IAsyncDisposable
{
    private readonly IReadOnlyList<string> _stunServers;
    private readonly Func<IDataChannelMessage, Task> _sendAsync;
    private readonly Func<string, Task> _onControlMessage;
    private readonly Func<FrameBinaryHeader, byte[], Task> _onFrameMessage;
    private readonly Func<RawImage, Task>? _onVideoFrame;
    private RTCPeerConnection? _peerConnection;
    private RTCDataChannel? _controlChannel;
    private RTCDataChannel? _frameChannel;

    public WebRtcOperator(
        IReadOnlyList<string> stunServers,
        Func<IDataChannelMessage, Task> sendAsync,
        Func<string, Task> onControlMessage,
        Func<FrameBinaryHeader, byte[], Task> onFrameMessage,
        Func<RawImage, Task>? onVideoFrame = null)
    {
        _stunServers = stunServers;
        _sendAsync = sendAsync;
        _onControlMessage = onControlMessage;
        _onFrameMessage = onFrameMessage;
        _onVideoFrame = onVideoFrame;
    }

    public async Task HandleOfferAsync(SdpOffer offer)
    {
        await ResetAsync().ConfigureAwait(false);

        var config = new RTCConfiguration
        {
            iceServers = _stunServers.Select(url => new RTCIceServer { urls = new List<string> { url } }).ToList()
        };

        _peerConnection = new RTCPeerConnection(config);
        _peerConnection.onicecandidate += candidate =>
        {
            if (candidate is null)
            {
                return;
            }

            _ = _sendAsync(new IceCandidate(candidate.toString(), candidate.sdpMid, candidate.sdpMLineIndex));
        };

        _peerConnection.onconnectionstatechange += state =>
        {
            _ = _sendAsync(new IceState(state.ToString()));
        };

        _peerConnection.ondatachannel += channel => SetupDataChannel(channel);
        _peerConnection.OnVideoFrameReceived += (_, frame) =>
        {
            if (_onVideoFrame is null)
            {
                return Task.CompletedTask;
            }

            return _onVideoFrame(frame);
        };

        SetupDataChannel(_peerConnection.createDataChannel("control", null));
        SetupDataChannel(_peerConnection.createDataChannel("frames", null));

        var remote = new RTCSessionDescriptionInit
        {
            sdp = offer.Sdp,
            type = RTCSdpType.offer
        };

        await _peerConnection.setRemoteDescription(remote).ConfigureAwait(false);
        var answer = _peerConnection.createAnswer(null);
        await _peerConnection.setLocalDescription(answer).ConfigureAwait(false);

        await _sendAsync(new SdpAnswer(answer.sdp, answer.type.ToString())).ConfigureAwait(false);
    }

    public Task AddRemoteCandidateAsync(IceCandidate candidate)
    {
        if (_peerConnection is null)
        {
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

    public async ValueTask DisposeAsync()
    {
        await ResetAsync().ConfigureAwait(false);
    }

    public async Task<bool> TrySendControlMessageAsync(IDataChannelMessage message)
    {
        if (_controlChannel is null || _controlChannel.readyState != RTCDataChannelState.open)
        {
            return false;
        }

        var payload = System.Text.Json.JsonSerializer.Serialize(message, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });

        var buffer = System.Text.Encoding.UTF8.GetBytes(payload);
        await _controlChannel.send(buffer).ConfigureAwait(false);
        return true;
    }

    private Task ResetAsync()
    {
        if (_peerConnection is not null)
        {
            _peerConnection.close("reset");
            _peerConnection.Dispose();
            _peerConnection = null;
        }

        _controlChannel = null;
        _frameChannel = null;

        return Task.CompletedTask;
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
                Console.WriteLine($"[webrtc] ignoring unexpected data channel {channel.label}");
                break;
        }
    }

    private void SetupControlChannel(RTCDataChannel channel)
    {
        _controlChannel = channel;
        channel.onopen += () => Console.WriteLine("[webrtc] control channel open");
        channel.onclose += () => Console.WriteLine("[webrtc] control channel closed");

        channel.onmessage += (_, proto, data) =>
        {
            if (data is null || data.Length == 0)
            {
                return;
            }

            var text = System.Text.Encoding.UTF8.GetString(data);
            _ = _onControlMessage(text);
        };
    }

    private void SetupFrameChannel(RTCDataChannel channel)
    {
        _frameChannel = channel;
        channel.onopen += () => Console.WriteLine("[webrtc] frame channel open");
        channel.onclose += () => Console.WriteLine("[webrtc] frame channel closed");

        channel.onmessage += (_, proto, data) =>
        {
            if (data is null || data.Length == 0)
            {
                return;
            }

            var separatorIndex = Array.IndexOf(data, (byte)0);
            if (separatorIndex <= 0 || separatorIndex >= data.Length - 1)
            {
                Console.WriteLine("[webrtc] frame channel received malformed payload");
                return;
            }

            var headerJson = System.Text.Encoding.UTF8.GetString(data, 0, separatorIndex);
            var header = System.Text.Json.JsonSerializer.Deserialize<FrameBinaryHeader>(headerJson, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

            if (header is null)
            {
                Console.WriteLine("[webrtc] frame channel header could not be parsed");
                return;
            }

            var payload = new byte[data.Length - separatorIndex - 1];
            Buffer.BlockCopy(data, separatorIndex + 1, payload, 0, payload.Length);
            _ = _onFrameMessage(header, payload);
        };
    }
}
