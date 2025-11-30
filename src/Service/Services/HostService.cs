using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Config;
using RemoteDesktop.Shared.Messaging;
using RemoteDesktop.Shared.Models;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Service.Services;

public sealed class HostService : BackgroundService
{
    private readonly HostConfigProvider _configProvider;
    private readonly LockoutManager _lockoutManager;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SignalingResolver _signalingResolver;
    private readonly WebSocketSignalingClient _signalingClient;
    private readonly SessionManager _sessionManager;
    private readonly MonitorService _monitorService;
    private readonly CaptureService _captureService;
    private readonly InputService _inputService;
    private readonly WebRtcService _webRtcService;
    private readonly ILogger<HostService> _logger;

    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private IReadOnlyList<MonitorDescriptor> _monitors = Array.Empty<MonitorDescriptor>();
    private string _activeMonitorId = "primary";
    private SessionManager.SessionLease? _sessionLease;
    private Guid _hostId;
    private HostConfig? _config;
    private CancellationTokenSource? _frameLoopCts;
    private Task? _frameLoopTask;
    private bool _authenticated;
    private bool _controlChannelReady;
    private bool _frameChannelReady;
    private DateTimeOffset _lastWebRtcRestart = DateTimeOffset.MinValue;
    private Uri? _currentSignalingEndpoint;
    private bool _signalingConnected;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly TimeSpan _resolverPollInterval = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _resolverMaxBackoff = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _resolverInitialBackoff = TimeSpan.FromSeconds(5);

    public HostService(
        HostConfigProvider configProvider,
        LockoutManager lockoutManager,
        IPasswordHasher passwordHasher,
        SignalingResolver signalingResolver,
        WebSocketSignalingClient signalingClient,
        SessionManager sessionManager,
        MonitorService monitorService,
        CaptureService captureService,
        InputService inputService,
        WebRtcService webRtcService,
        ILogger<HostService> logger)
    {
        _configProvider = configProvider;
        _lockoutManager = lockoutManager;
        _passwordHasher = passwordHasher;
        _signalingResolver = signalingResolver;
        _signalingClient = signalingClient;
        _sessionManager = sessionManager;
        _monitorService = monitorService;
        _captureService = captureService;
        _inputService = inputService;
        _webRtcService = webRtcService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Host service booting...");
        _config = await _configProvider.GetAsync(stoppingToken).ConfigureAwait(false);
        _hostId = _config.HostId;
        _monitors = _monitorService.Enumerate();
        _activeMonitorId = _monitors.FirstOrDefault()?.Id ?? _activeMonitorId;
        _logger.LogInformation("Host ID: {HostId}; resolver: {Resolver}", _config.HostId, _config.SignalingResolverUrl);

        _signalingClient.SetMessageHandler(OnSignalingMessageAsync);
        _signalingClient.Disconnected += OnSignalingDisconnected;

        _webRtcService.OfferReady += offer => SendAsync(offer, stoppingToken);
        _webRtcService.IceCandidateReady += candidate => SendAsync(candidate, stoppingToken);
        _webRtcService.IceStateChanged += state => HandleIceStateChangedAsync(state, stoppingToken);
        _webRtcService.ControlChannelOpened += () =>
        {
            _controlChannelReady = true;
            _logger.LogInformation("Control data channel ready; switching to data-channel delivery when possible");
            return Task.CompletedTask;
        };
        _webRtcService.ControlChannelClosed += () =>
        {
            _controlChannelReady = false;
            _logger.LogInformation("Control data channel closed; falling back to signaling transport");
            return Task.CompletedTask;
        };
        _webRtcService.FrameChannelOpened += () =>
        {
            _frameChannelReady = true;
            _logger.LogInformation("Frame data channel ready; streaming frames over WebRTC");
            return Task.CompletedTask;
        };
        _webRtcService.FrameChannelClosed += () =>
        {
            _frameChannelReady = false;
            _logger.LogInformation("Frame data channel closed; falling back to control/signaling for frames");
            return Task.CompletedTask;
        };
        _webRtcService.ControlMessageReceived += message => OnSignalingMessageAsync(message, stoppingToken);

        await InitializeNetworkingAsync(_config, stoppingToken).ConfigureAwait(false);
        await InitializeCaptureAsync(stoppingToken).ConfigureAwait(false);

        var resolverLoop = RunResolverLoopAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when shutting down the host.
        }

        await resolverLoop.ConfigureAwait(false);
    }

    private async Task InitializeNetworkingAsync(HostConfig config, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Preparing signaling and WebRTC stacks (stun: {StunCount})", config.StunServers.Count);

        var resolved = await _signalingResolver.ResolveAsync(config.SignalingResolverUrl, cancellationToken).ConfigureAwait(false);
        if (resolved is null && Uri.TryCreate(config.SignalingResolverUrl, UriKind.Absolute, out var fallback))
        {
            resolved = fallback;
        }

        _currentSignalingEndpoint = resolved;

        if (resolved is null)
        {
            _logger.LogWarning("No signaling endpoint available; service will keep retrying on next resolver poll");
            return;
        }

        await TryConnectAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunResolverLoopAsync(CancellationToken cancellationToken)
    {
        var backoff = _resolverInitialBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_config is not HostConfig config)
                {
                    _logger.LogWarning("Skipping resolver poll because config is unavailable");
                    await Task.Delay(_resolverPollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var resolved = await _signalingResolver.ResolveAsync(config.SignalingResolverUrl, cancellationToken).ConfigureAwait(false);
                if (resolved is null && Uri.TryCreate(config.SignalingResolverUrl, UriKind.Absolute, out var fallback))
                {
                    resolved = fallback;
                }

                if (resolved is not null)
                {
                    var changed = _currentSignalingEndpoint is null || _currentSignalingEndpoint != resolved;
                    _currentSignalingEndpoint = resolved;
                    backoff = _resolverInitialBackoff;

                    if (!_signalingConnected || changed)
                    {
                        await TryConnectAsync(resolved, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    _logger.LogWarning("Resolver did not return a usable endpoint; backing off");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resolver loop iteration failed");
            }

            var delay = _currentSignalingEndpoint is null
                ? TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds, _resolverMaxBackoff.TotalMilliseconds))
                : _resolverPollInterval;

            if (_currentSignalingEndpoint is null)
            {
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, _resolverMaxBackoff.TotalMilliseconds));
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private Task InitializeCaptureAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing capture pipeline (DXGI primary, GDI fallback)");
        // TODO: Implement DXGI Desktop Duplication and fallback logic.
        return Task.CompletedTask;
    }

    public async Task<AuthResult> HandleAuthenticationAsync(string password, CancellationToken cancellationToken)
    {
        if (await _lockoutManager.IsLockedAsync(cancellationToken).ConfigureAwait(false))
        {
            var config = await _configProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            var retry = config.Lockout.LockedUntil.HasValue
                ? (int)Math.Max(0, (config.Lockout.LockedUntil.Value - DateTimeOffset.UtcNow).TotalMilliseconds)
                : (int)TimeSpan.FromMinutes(5).TotalMilliseconds;

            return new AuthResult("locked", retry);
        }

        var configSnapshot = await _configProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        if (_passwordHasher.Verify(password, configSnapshot.PasswordHash))
        {
            await _lockoutManager.RegisterSuccessAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Authentication succeeded for host {HostId}", configSnapshot.HostId);
            _authenticated = true;
            EnsureFrameLoop();
            return new AuthResult("ok", null);
        }

        var lockout = await _lockoutManager.RegisterFailureAsync(cancellationToken).ConfigureAwait(false);
        var retryAfter = lockout.LockedUntil.HasValue
            ? (int)Math.Max(0, (lockout.LockedUntil.Value - DateTimeOffset.UtcNow).TotalMilliseconds)
            : null;

        _logger.LogWarning("Authentication failed for host {HostId}; attempts={Attempts}", configSnapshot.HostId, lockout.FailedAttempts);
        return new AuthResult("invalid", retryAfter);
    }

    private async Task OnSignalingMessageAsync(string payload, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("type", out var typeElement))
        {
            _logger.LogWarning("Dropping signaling message without type: {Payload}", payload);
            return;
        }

        var type = typeElement.GetString();
        switch (type)
        {
            case SignalingMessageTypes.OperatorHello:
                {
                    var sessionId = doc.RootElement.TryGetProperty("session_id", out var idElement)
                        ? idElement.GetString()
                        : null;
                    await HandleOperatorHelloAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    break;
                }
            case MessageTypes.Auth:
                {
                    var password = doc.RootElement.TryGetProperty("password", out var passwordElement)
                        ? passwordElement.GetString() ?? string.Empty
                        : string.Empty;
                    var result = await HandleAuthenticationAsync(password, cancellationToken).ConfigureAwait(false);
                    await SendControlAsync(result, cancellationToken).ConfigureAwait(false);
                    break;
                }
            case MessageTypes.MonitorSwitch:
                {
                    var id = doc.RootElement.TryGetProperty("id", out var idElement)
                        ? idElement.GetString() ?? string.Empty
                        : string.Empty;
                    await HandleMonitorSwitchAsync(id, cancellationToken).ConfigureAwait(false);
                    break;
                }
            case MessageTypes.Input:
                {
                    var input = JsonSerializer.Deserialize<InputMessage>(payload, _serializerOptions);
                    if (!_authenticated)
                    {
                        _logger.LogWarning("Ignoring input while unauthenticated");
                        break;
                    }

                    if (input is not null)
                    {
                        await _inputService.HandleAsync(input, _activeMonitorId, cancellationToken).ConfigureAwait(false);
                    }
                    break;
                }
            case SignalingMessageTypes.MonitorListRequest:
            case MessageTypes.MonitorList:
                await SendMonitorListAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SignalingMessageTypes.SdpAnswer:
                {
                    var answer = JsonSerializer.Deserialize<SdpAnswer>(payload, _serializerOptions);
                    if (answer is not null)
                    {
                        await _webRtcService.AcceptAnswerAsync(answer).ConfigureAwait(false);
                    }

                    break;
                }
            case SignalingMessageTypes.IceCandidate:
                {
                    var candidate = JsonSerializer.Deserialize<IceCandidate>(payload, _serializerOptions);
                    if (candidate is not null)
                    {
                        await _webRtcService.AddRemoteCandidateAsync(candidate).ConfigureAwait(false);
                    }

                    break;
                }
            default:
                _logger.LogDebug("Unhandled signaling message type {Type}", type);
                break;
        }
    }

    private async Task HandleOperatorHelloAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _logger.LogWarning("Received operator hello without a session id");
            return;
        }

        if (_sessionLease is not null && _sessionLease.Value.SessionId != sessionId)
        {
            await SendControlAsync(new HostBusy("busy"), cancellationToken).ConfigureAwait(false);
            return;
        }

        var lease = _sessionLease ?? await _sessionManager.TryBeginAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            await SendControlAsync(new HostBusy("busy"), cancellationToken).ConfigureAwait(false);
            return;
        }

        _sessionLease = lease;
        _authenticated = false;
        StopFrameLoop();
        await SendHostHelloAsync(cancellationToken).ConfigureAwait(false);
        await SendMonitorListAsync(cancellationToken).ConfigureAwait(false);
        if (_config is not null)
        {
            await _webRtcService.StartOfferAsync(_config.StunServers, _config.Turn, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleMonitorSwitchAsync(string monitorId, CancellationToken cancellationToken)
    {
        if (_monitors.All(m => m.Id != monitorId))
        {
            _logger.LogWarning("Requested monitor {MonitorId} does not exist", monitorId);
            return;
        }

        _activeMonitorId = monitorId;
        await SendControlAsync(new MonitorSwitchResult(_activeMonitorId), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMonitorListAsync(CancellationToken cancellationToken)
    {
        var message = new MonitorList(_monitors, _activeMonitorId);
        await SendControlAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendHostHelloAsync(CancellationToken cancellationToken)
    {
        if (_monitors.Count == 0)
        {
            _logger.LogWarning("No monitors available to advertise");
        }

        var hello = new HostHello(_hostId, _monitors, _activeMonitorId);
        await SendControlAsync(hello, cancellationToken).ConfigureAwait(false);
    }

    private Task SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(message, _serializerOptions);
        return _signalingClient.SendTextAsync(payload, cancellationToken);
    }

    private async Task SendControlAsync(IDataChannelMessage message, CancellationToken cancellationToken)
    {
        if (_controlChannelReady && await _webRtcService.TrySendControlMessageAsync(message, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleIceStateChangedAsync(string state, CancellationToken cancellationToken)
    {
        await SendAsync(new IceState(state), cancellationToken).ConfigureAwait(false);

        if (!(_config is HostConfig config) || _sessionLease is null)
        {
            return;
        }

        if (!state.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
            !state.Equals("disconnected", StringComparison.OrdinalIgnoreCase) &&
            !state.Equals("closed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastWebRtcRestart < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastWebRtcRestart = now;
        _logger.LogInformation("WebRTC state {State}; re-offering to recover", state);
        await _webRtcService.StartOfferAsync(config.StunServers, config.Turn, cancellationToken).ConfigureAwait(false);
    }

    private void OnSignalingDisconnected()
    {
        if (_sessionLease is SessionManager.SessionLease lease)
        {
            _ = lease.DisposeAsync();
        }

        _sessionLease = null;
        _authenticated = false;
        _controlChannelReady = false;
        _frameChannelReady = false;
        StopFrameLoop();
        _ = _webRtcService.ResetAsync();
        _signalingConnected = false;

        if (_currentSignalingEndpoint is Uri endpoint)
        {
            _ = TryReconnectAsync(endpoint);
        }
    }

    private async Task TryReconnectAsync(Uri endpoint)
    {
        try
        {
            await TryConnectAsync(endpoint, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.WebSockets.WebSocketException)
        {
            _logger.LogWarning(ex, "Failed to reconnect to signaling server {Endpoint}", endpoint);
        }
    }

    private async Task TryConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (endpoint is null)
        {
            return;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_signalingConnected && _currentSignalingEndpoint == endpoint)
            {
                return;
            }

            await _signalingClient.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            _signalingConnected = true;
            await SendHostHelloAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void EnsureFrameLoop()
    {
        if (_frameLoopTask is not null && !_frameLoopTask.IsCompleted)
        {
            return;
        }

        _frameLoopCts = new CancellationTokenSource();
        var token = _frameLoopCts.Token;
        _frameLoopTask = Task.Run(async () =>
        {
            _logger.LogInformation("Starting frame loop for monitor {MonitorId}", _activeMonitorId);
            try
            {
                while (!token.IsCancellationRequested && _sessionLease is not null && _authenticated)
                {
                    var frame = _captureService.Capture(_activeMonitorId);
                    var sentOverVideoTrack = _webRtcService.HasVideoTrack &&
                        await _webRtcService.TrySendVideoFrameAsync(frame, token).ConfigureAwait(false);

                    var sentOverRtc = !sentOverVideoTrack && _frameChannelReady && await _webRtcService.TrySendFrameAsync(
                        new FrameBinaryHeader(frame.Width, frame.Height, frame.Format),
                        frame.PngData,
                        token).ConfigureAwait(false);

                    if (!sentOverRtc && !sentOverVideoTrack)
                    {
                        var payload = new FrameChunk(frame.Width, frame.Height, frame.Format, Convert.ToBase64String(frame.PngData));
                        await SendControlAsync(payload, token).ConfigureAwait(false);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down the loop.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Frame loop faulted");
            }
            finally
            {
                _logger.LogInformation("Frame loop stopped");
            }
        }, token);
    }

    private void StopFrameLoop()
    {
        _frameLoopCts?.Cancel();
        _frameLoopCts = null;
        _frameLoopTask = null;
    }
}
