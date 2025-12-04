using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Config;
using RemoteDesktop.Shared.Messaging;
using SIPSorcery.Net;

namespace RemoteDesktop.Service.Services;

public sealed class WebRtcService : IDisposable
{
    private readonly ILogger<WebRtcService> _logger;
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _controlChannel;
    private RTCDataChannel? _frameChannel;

    // События для HostService
    public event Func<object, Task>? OfferReady;
    public event Func<object, Task>? IceCandidateReady;
    public event Func<string, Task>? IceStateChanged;
    public event Func<Task>? ControlChannelOpened;
    public event Func<Task>? ControlChannelClosed;
    public event Func<Task>? FrameChannelOpened;
    public event Func<Task>? FrameChannelClosed;
    public event Func<string, Task>? ControlMessageReceived;

    // Свойство, которое ищет HostService. Пока false, чтобы использовать DataChannel для видео.
    public bool HasVideoTrack => false;

    public WebRtcService(ILogger<WebRtcService> logger)
    {
        _logger = logger;
    }

    public async Task StartOfferAsync(IReadOnlyList<string> stunServers, TurnConfig turn, CancellationToken cancellationToken)
    {
        await ResetAsync();

        var config = new RTCConfiguration
        {
            iceServers = stunServers.Select(s => new RTCIceServer { urls = s }).ToList()
        };

        if (!string.IsNullOrEmpty(turn.Url))
        {
            config.iceServers.Add(new RTCIceServer 
            { 
                urls = turn.Url, 
                username = turn.Username, 
                credential = turn.Credential 
            });
        }

        _pc = new RTCPeerConnection(config);

        _pc.onicecandidate += async (candidate) =>
        {
            if (IceCandidateReady != null && candidate != null)
            {
                var msg = new IceCandidate(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
                await IceCandidateReady(msg);
            }
        };

        _pc.onconnectionstatechange += async (state) =>
        {
            _logger.LogInformation("ICE State: {State}", state);
            if (IceStateChanged != null)
            {
                await IceStateChanged(state.ToString().ToLower());
            }
        };

        // Создаем каналы данных
        var control = await _pc.createDataChannel("control");
        SetupControlChannel(control);

        var frames = await _pc.createDataChannel("frames");
        SetupFrameChannel(frames);

        var offer = _pc.createOffer(null);
        await _pc.setLocalDescription(offer);

        if (OfferReady != null)
        {
            await OfferReady(new SdpOffer(offer.sdp, offer.type.ToString()));
        }
    }

    public async Task AcceptAnswerAsync(SdpAnswer answer)
    {
        if (_pc == null) return;
        
        var remoteDesc = new RTCSessionDescriptionInit 
        { 
            sdp = answer.Sdp, 
            type = RTCSdpType.answer 
        };
        
        await _pc.setRemoteDescription(remoteDesc);
    }

    public async Task AddRemoteCandidateAsync(IceCandidate candidate)
    {
        if (_pc == null) return;

        var init = new RTCIceCandidateInit 
        { 
            candidate = candidate.Candidate, 
            sdpMid = candidate.SdpMid, 
            sdpMLineIndex = (ushort?)candidate.SdpMLineIndex ?? 0
        };
        
        _pc.addIceCandidate(init);
    }

    // Заглушка для отправки видео через трек (пока не используем)
    public Task<bool> TrySendVideoFrameAsync(CapturedFrame frame, CancellationToken token)
    {
        return Task.FromResult(false);
    }

    // Отправка кадров через DataChannel (это будет работать)
    public async Task<bool> TrySendFrameAsync(FrameBinaryHeader header, byte[] data, CancellationToken token)
    {
        if (_frameChannel == null || _frameChannel.readyState != RTCDataChannelState.open)
        {
            return false;
        }

        try
        {
            var headerJson = JsonSerializer.Serialize(header, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var headerBytes = System.Text.Encoding.UTF8.GetBytes(headerJson);
            
            // Формат: [Header][0x00][Data]
            var packet = new byte[headerBytes.Length + 1 + data.Length];
            Buffer.BlockCopy(headerBytes, 0, packet, 0, headerBytes.Length);
            packet[headerBytes.Length] = 0; // null terminator separator
            Buffer.BlockCopy(data, 0, packet, headerBytes.Length + 1, data.Length);

            _frameChannel.send(packet);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send frame via DataChannel");
            return false;
        }
    }

    public async Task<bool> TrySendControlMessageAsync(IDataChannelMessage message, CancellationToken token)
    {
        if (_controlChannel == null || _controlChannel.readyState != RTCDataChannelState.open)
        {
            return false;
        }

        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        _controlChannel.send(System.Text.Encoding.UTF8.GetBytes(json));
        return true;
    }

    public Task ResetAsync()
    {
        _pc?.Close("reset");
        _pc?.Dispose();
        _pc = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        ResetAsync();
    }

    private void SetupControlChannel(RTCDataChannel channel)
    {
        _controlChannel = channel;
        channel.onopen += () => ControlChannelOpened?.Invoke();
        channel.onclose += () => ControlChannelClosed?.Invoke();
        channel.onmessage += (dc, proto, data) => 
        {
            var text = System.Text.Encoding.UTF8.GetString(data);
            ControlMessageReceived?.Invoke(text);
        };
    }

    private void SetupFrameChannel(RTCDataChannel channel)
    {
        _frameChannel = channel;
        channel.onopen += () => FrameChannelOpened?.Invoke();
        channel.onclose += () => FrameChannelClosed?.Invoke();
    }
}