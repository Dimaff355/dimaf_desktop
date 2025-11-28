using System.Text.Json.Serialization;
using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Shared.Messaging;

public static class SignalingMessageTypes
{
    public const string OperatorHello = "operator_hello";
    public const string HostHello = "host_hello";
    public const string MonitorListRequest = "monitor_list_request";
    public const string MonitorSwitchResult = "monitor_switch_result";
}

public sealed record OperatorHello(
    [property: JsonPropertyName("session_id")] string SessionId
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => SignalingMessageTypes.OperatorHello;
}

public sealed record HostHello(
    [property: JsonPropertyName("host_id")] Guid HostId,
    [property: JsonPropertyName("monitors")] IReadOnlyList<MonitorDescriptor> Monitors,
    [property: JsonPropertyName("active_monitor_id")] string ActiveMonitorId
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => SignalingMessageTypes.HostHello;
}

public sealed record MonitorListRequest(
    [property: JsonPropertyName("session_id")] string SessionId
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => SignalingMessageTypes.MonitorListRequest;
}

public sealed record MonitorSwitchResult(
    [property: JsonPropertyName("active_monitor_id")] string ActiveMonitorId
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => SignalingMessageTypes.MonitorSwitchResult;
}
