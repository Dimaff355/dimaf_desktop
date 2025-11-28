using System.Text.Json.Serialization;

namespace RemoteDesktop.Shared.Config;

public sealed record HostConfig
{
    [JsonPropertyName("host_id")]
    public Guid HostId { get; init; }

    [JsonPropertyName("password_hash")]
    public string PasswordHash { get; init; } = string.Empty;

    [JsonPropertyName("signaling_resolver_url")]
    public string SignalingResolverUrl { get; init; } = string.Empty;

    [JsonPropertyName("stun")]
    public IReadOnlyList<string> StunServers { get; init; } = Array.Empty<string>();

    [JsonPropertyName("turn")]
    public TurnConfig Turn { get; init; } = new();

    [JsonPropertyName("logging")]
    public LoggingConfig Logging { get; init; } = new();

    [JsonPropertyName("lockout")]
    public LockoutConfig Lockout { get; init; } = new();

    public static HostConfig CreateDefault()
    {
        return new HostConfig
        {
            HostId = Guid.NewGuid(),
            PasswordHash = string.Empty,
            SignalingResolverUrl = string.Empty,
            StunServers = new[] { "stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302" },
            Turn = new TurnConfig(),
            Logging = new LoggingConfig(),
            Lockout = new LockoutConfig()
        };
    }
}

public sealed class TurnConfig
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("credential")]
    public string Credential { get; init; } = string.Empty;
}

public sealed class LoggingConfig
{
    [JsonPropertyName("max_bytes")]
    public int MaxBytes { get; init; } = 10 * 1024 * 1024;

    [JsonPropertyName("files")]
    public int Files { get; init; } = 5;
}

public sealed class LockoutConfig
{
    [JsonPropertyName("failed_attempts")]
    public int FailedAttempts { get; init; }

    [JsonPropertyName("locked_until")]
    public DateTimeOffset? LockedUntil { get; init; }
}
