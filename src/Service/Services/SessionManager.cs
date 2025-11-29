using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Service.Services;

public sealed class SessionManager
{
    private readonly ILogger<SessionManager> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private string? _activeSessionId;

    public SessionManager(ILogger<SessionManager> logger)
    {
        _logger = logger;
    }

    public async Task<SessionLease?> TryBeginAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeSessionId is not null && _activeSessionId != sessionId)
            {
                _logger.LogWarning("Host is busy with session {SessionId}", _activeSessionId);
                return null;
            }

            _activeSessionId = sessionId;
            _logger.LogInformation("Acquired session {SessionId}", sessionId);
            return new SessionLease(this, sessionId);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task ReleaseAsync(string sessionId)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeSessionId == sessionId)
            {
                _logger.LogInformation("Releasing session {SessionId}", sessionId);
                _activeSessionId = null;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public readonly struct SessionLease : IAsyncDisposable
    {
        private readonly SessionManager _owner;
        public string SessionId { get; }

        public SessionLease(SessionManager owner, string sessionId)
        {
            _owner = owner;
            SessionId = sessionId;
        }

        public ValueTask DisposeAsync()
        {
            return _owner.ReleaseAsync(SessionId);
        }
    }
}
