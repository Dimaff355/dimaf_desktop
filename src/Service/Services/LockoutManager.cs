using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Config;

namespace RemoteDesktop.Service.Services;

public sealed class LockoutManager
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private readonly HostConfigProvider _configProvider;
    private readonly ILogger<LockoutManager> _logger;

    public LockoutManager(HostConfigProvider configProvider, ILogger<LockoutManager> logger)
    {
        _configProvider = configProvider;
        _logger = logger;
    }

    public async Task<LockoutConfig> RegisterFailureAsync(CancellationToken cancellationToken)
    {
        var config = await _configProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var attempts = config.Lockout.FailedAttempts + 1;
        var lockedUntil = config.Lockout.LockedUntil;

        if (attempts >= MaxAttempts)
        {
            lockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
            attempts = 0;
            _logger.LogWarning("Locking host until {LockedUntil} due to repeated authentication failures", lockedUntil);
        }

        var updated = config with
        {
            Lockout = new LockoutConfig
            {
                FailedAttempts = attempts,
                LockedUntil = lockedUntil
            }
        };

        await _configProvider.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated.Lockout;
    }

    public async Task<LockoutConfig> RegisterSuccessAsync(CancellationToken cancellationToken)
    {
        var config = await _configProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        if (config.Lockout.FailedAttempts == 0 && config.Lockout.LockedUntil is null)
        {
            return config.Lockout;
        }

        var updated = config with
        {
            Lockout = new LockoutConfig
            {
                FailedAttempts = 0,
                LockedUntil = null
            }
        };

        await _configProvider.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Cleared lockout state after successful authentication");
        return updated.Lockout;
    }

    public async Task<bool> IsLockedAsync(CancellationToken cancellationToken)
    {
        var config = await _configProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        if (config.Lockout.LockedUntil is null)
        {
            return false;
        }

        if (config.Lockout.LockedUntil > DateTimeOffset.UtcNow)
        {
            return true;
        }

        await RegisterSuccessAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }
}
