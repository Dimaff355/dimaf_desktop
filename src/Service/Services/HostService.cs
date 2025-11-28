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

    public HostService(
        HostConfigProvider configProvider,
        LockoutManager lockoutManager,
        IPasswordHasher passwordHasher,
        SignalingResolver signalingResolver,
        WebSocketSignalingClient signalingClient,
        SessionManager sessionManager,
        MonitorService monitorService,
        ILogger<HostService> logger)
    {
        _configProvider = configProvider;
        _lockoutManager = lockoutManager;
        _passwordHasher = passwordHasher;
        _signalingResolver = signalingResolver;
        _signalingClient = signalingClient;
        _sessionManager = sessionManager;
        _monitorService = monitorService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Host service booting...");
        var config = await _configProvider.GetAsync(stoppingToken).ConfigureAwait(false);
        _hostId = config.HostId;
        _monitors = _monitorService.Enumerate();
        _activeMonitorId = _monitors.FirstOrDefault()?.Id ?? _activeMonitorId;
        _logger.LogInformation("Host ID: {HostId}; resolver: {Resolver}", config.HostId, config.SignalingResolverUrl);

        _signalingClient.SetMessageHandler(OnSignalingMessageAsync);
        _signalingClient.Disconnected += OnSignalingDisconnected;

        await InitializeNetworkingAsync(config, stoppingToken).ConfigureAwait(false);
        await InitializeCaptureAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task InitializeNetworkingAsync(HostConfig config, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Preparing signaling and WebRTC stacks (stun: {StunCount})", config.StunServers.Count);

        var resolved = await _signalingResolver.ResolveAsync(config.SignalingResolverUrl, cancellationToken).ConfigureAwait(false);
        if (resolved is null && Uri.TryCreate(config.SignalingResolverUrl, UriKind.Absolute, out var fallback))
        {
            resolved = fallback;
        }

        if (resolved is null)
        {
            _logger.LogWarning("No signaling endpoint available; service will keep retrying on next resolver poll");
            return;
        }

        try
        {
            await _signalingClient.ConnectAsync(resolved, cancellationToken).ConfigureAwait(false);
            await SendHostHelloAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.Net.WebSockets.WebSocketException)
        {
            _logger.LogWarning(ex, "Failed to connect to signaling server {Endpoint}", resolved);
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
                    await SendAsync(result, cancellationToken).ConfigureAwait(false);
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
            case SignalingMessageTypes.MonitorListRequest:
            case MessageTypes.MonitorList:
                await SendMonitorListAsync(cancellationToken).ConfigureAwait(false);
                break;
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
            await SendAsync(new HostBusy("busy"), cancellationToken).ConfigureAwait(false);
            return;
        }

        var lease = _sessionLease ?? await _sessionManager.TryBeginAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            await SendAsync(new HostBusy("busy"), cancellationToken).ConfigureAwait(false);
            return;
        }

        _sessionLease = lease;
        await SendHostHelloAsync(cancellationToken).ConfigureAwait(false);
        await SendMonitorListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleMonitorSwitchAsync(string monitorId, CancellationToken cancellationToken)
    {
        if (_monitors.All(m => m.Id != monitorId))
        {
            _logger.LogWarning("Requested monitor {MonitorId} does not exist", monitorId);
            return;
        }

        _activeMonitorId = monitorId;
        await SendAsync(new MonitorSwitchResult(_activeMonitorId), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMonitorListAsync(CancellationToken cancellationToken)
    {
        var message = new MonitorList(_monitors, _activeMonitorId);
        await SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendHostHelloAsync(CancellationToken cancellationToken)
    {
        if (_monitors.Count == 0)
        {
            _logger.LogWarning("No monitors available to advertise");
        }

        var hello = new HostHello(_hostId, _monitors, _activeMonitorId);
        await SendAsync(hello, cancellationToken).ConfigureAwait(false);
    }

    private Task SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(message, _serializerOptions);
        return _signalingClient.SendTextAsync(payload, cancellationToken);
    }

    private void OnSignalingDisconnected()
    {
        if (_sessionLease is SessionManager.SessionLease lease)
        {
            _ = lease.DisposeAsync();
        }

        _sessionLease = null;
    }
}
