using System.Text.Json.Serialization;
using RemoteDesktop.Shared.Config;

namespace RemoteDesktop.Shared.Messaging;

public static class IpcMessageTypes
{
    public const string Status = "status";
    public const string SetPassword = "set_password";
    public const string SetResolver = "set_resolver";
    public const string SetIce = "set_ice";
}

public sealed record IpcStatusRequest
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = IpcMessageTypes.Status;
}

public sealed record IpcStatusResponse
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = IpcMessageTypes.Status;

    [JsonPropertyName("host_id")]
    public Guid HostId { get; init; }

    [JsonPropertyName("has_password")]
    public bool HasPassword { get; init; }

    [JsonPropertyName("signaling_resolver_url")]
    public string SignalingResolverUrl { get; init; } = string.Empty;

    [JsonPropertyName("stun")]
    public IReadOnlyList<string> StunServers { get; init; } = Array.Empty<string>();

    [JsonPropertyName("turn")]
    public TurnConfig Turn { get; init; } = new();
}

public sealed record IpcSetPasswordRequest
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = IpcMessageTypes.SetPassword;

    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;
}

public sealed record IpcSetResolverRequest
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = IpcMessageTypes.SetResolver;

    [JsonPropertyName("resolver_url")]
    public string ResolverUrl { get; init; } = string.Empty;
}

public sealed record IpcSetIceRequest
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = IpcMessageTypes.SetIce;

    [JsonPropertyName("stun")]
    public IReadOnlyList<string> StunServers { get; init; } = Array.Empty<string>();

    [JsonPropertyName("turn_url")]
    public string TurnUrl { get; init; } = string.Empty;

    [JsonPropertyName("turn_username")]
    public string TurnUsername { get; init; } = string.Empty;

    [JsonPropertyName("turn_credential")]
    public string TurnCredential { get; init; } = string.Empty;
}

public sealed record IpcCommandResult
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "result";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "ok";

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
