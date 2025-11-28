using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Config;
using RemoteDesktop.Shared.Messaging;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Service.Services;

public sealed class HostService : BackgroundService
{
    private readonly HostConfigProvider _configProvider;
    private readonly LockoutManager _lockoutManager;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<HostService> _logger;

    public HostService(
        HostConfigProvider configProvider,
        LockoutManager lockoutManager,
        IPasswordHasher passwordHasher,
        ILogger<HostService> logger)
    {
        _configProvider = configProvider;
        _lockoutManager = lockoutManager;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Host service booting...");
        var config = await _configProvider.GetAsync(stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("Host ID: {HostId}; resolver: {Resolver}", config.HostId, config.SignalingResolverUrl);

        await InitializeNetworkingAsync(config, stoppingToken).ConfigureAwait(false);
        await InitializeCaptureAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private Task InitializeNetworkingAsync(HostConfig config, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Preparing signaling and WebRTC stacks (stun: {StunCount})", config.StunServers.Count);
        // TODO: Wire up WebSocket signaling, resolver polling, and WebRTC peer connection.
        return Task.CompletedTask;
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
}
