using System.Text.Json.Serialization;
using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Shared.Messaging;

public static class SignalingMessageTypes
{
    public const string OperatorHello = "operator_hello";
    public const string HostHello = "host_hello";
    public const string MonitorListRequest = "monitor_list_request";
    public const string MonitorSwitchResult = "monitor_switch_result";
    public const string SdpOffer = "sdp_offer";
    public const string SdpAnswer = "sdp_answer";
    public const string IceCandidate = "ice_candidate";
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

public sealed record SdpOffer(
    [property: JsonPropertyName("sdp")] string Sdp,
    [property: JsonPropertyName("sdp_type")] string SdpType
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => SignalingMessageTypes.SdpOffer;
}

public sealed record SdpAnswer(
    [property: JsonPropertyName("sdp")] string Sdp,
    [property: JsonPropertyName("sdp_type")] string SdpType
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => SignalingMessageTypes.SdpAnswer;
}

public sealed record IceCandidate(
    [property: JsonPropertyName("candidate")] string Candidate,
    [property: JsonPropertyName("sdp_mid")] string? SdpMid,
    [property: JsonPropertyName("sdp_mline_index")] int? SdpMLineIndex
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => SignalingMessageTypes.IceCandidate;
}
