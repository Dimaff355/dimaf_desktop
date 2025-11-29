using System.Text.Json.Serialization;
using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Shared.Messaging;

public static class MessageTypes
{
    public const string Auth = "auth";
    public const string AuthResult = "auth_result";
    public const string MonitorList = "monitor_list";
    public const string MonitorSwitch = "monitor_switch";
    public const string Input = "input";
    public const string HostBusy = "host_busy";
    public const string IceState = "ice_state";
    public const string Frame = "frame";
}

public interface IDataChannelMessage
{
    string Type { get; }
}

public sealed record AuthRequest(
    [property: JsonPropertyName("password")] string Password
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.Auth;
}

public sealed record AuthResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("retry_after_ms")] int? RetryAfterMs
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.AuthResult;
}

public sealed record MonitorList(
    [property: JsonPropertyName("monitors")] IReadOnlyList<MonitorDescriptor> Monitors,
    [property: JsonPropertyName("active_monitor_id")] string ActiveMonitorId
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.MonitorList;
}

public sealed record MonitorSwitch(
    [property: JsonPropertyName("id")] string Id
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.MonitorSwitch;
}

public sealed record InputMessage(
    [property: JsonPropertyName("mouse")] MousePayload? Mouse,
    [property: JsonPropertyName("keyboard")] KeyboardPayload? Keyboard
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.Input;
}

public sealed record HostBusy(
    [property: JsonPropertyName("reason")] string Reason
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.HostBusy;
}

public sealed record IceState(
    [property: JsonPropertyName("state")] string State
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.IceState;
}

public sealed record FrameChunk(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("data")] string Data
) : IDataChannelMessage
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.Frame;
}

public sealed record FrameBinaryHeader(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("format")] string Format
);

public sealed record MousePayload(
    [property: JsonPropertyName("x")] double? X,
    [property: JsonPropertyName("y")] double? Y,
    [property: JsonPropertyName("wheel")] double? Wheel,
    [property: JsonPropertyName("buttons")] MouseButtons? Buttons
);

public sealed record MouseButtons(
    [property: JsonPropertyName("left")] bool? Left,
    [property: JsonPropertyName("right")] bool? Right,
    [property: JsonPropertyName("middle")] bool? Middle
);

public sealed record KeyboardPayload(
    [property: JsonPropertyName("scan_code")] int ScanCode,
    [property: JsonPropertyName("is_key_down")] bool IsKeyDown
);
