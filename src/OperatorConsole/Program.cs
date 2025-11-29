using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OperatorConsole;
using RemoteDesktop.Shared.Messaging;

if (args.Length < 2)
{
    Console.WriteLine("Usage: dotnet run --project src/OperatorConsole -- <signaling-ws-url> <host-id> [password]");
    return;
}

var endpoint = new Uri(args[0]);
if (!Guid.TryParse(args[1], out var hostId))
{
    Console.WriteLine("Host ID must be a GUID");
    return;
}

var password = args.Length > 2 ? args[2] : string.Empty;

var stun = new[] { "stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302" };
var serializerOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

var sessionId = Guid.NewGuid().ToString();
Console.WriteLine($"Connecting to {endpoint} for host {hostId} with session {sessionId}...");

using var client = new ClientWebSocket();
WebRtcOperator? webRtc = null;
webRtc = new WebRtcOperator(
    stun,
    message => SendAsync(client, message, serializerOptions),
    json => ProcessEnvelopeAsync(json, client, sessionId, password, webRtc!, serializerOptions, true),
    (header, payload) => SaveBinaryFrameAsync(header, payload));
await using var webRtcDisposable = webRtc;
{
    await client.ConnectAsync(new UriBuilder(endpoint) { Query = $"role=operator&hostId={hostId}" }.Uri, CancellationToken.None);
    await SendControlAsync(client, webRtc, new OperatorHello(sessionId), serializerOptions);

    using var cts = new CancellationTokenSource();
    var receiveTask = ReceiveLoopAsync(client, sessionId, password, webRtc, serializerOptions, cts.Token);
    var commandTask = CommandLoopAsync(client, webRtc, serializerOptions, cts.Token);

    Console.WriteLine("Commands: monitor_switch <id>, mouse <x 0..1> <y 0..1>, click <left|right|middle>, wheel <delta>, key <scanCode> <down|up>");
    Console.WriteLine("Press Ctrl+C to exit.");

    await Task.WhenAny(receiveTask, commandTask);
    cts.Cancel();
    await Task.WhenAll(receiveTask, commandTask);
}

static async Task ReceiveLoopAsync(ClientWebSocket socket, string sessionId, string password, WebRtcOperator webRtc, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
{
    while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
    {
        var json = await ReceiveTextAsync(socket, cancellationToken);
        if (json is null)
        {
            Console.WriteLine("Socket closed by server");
            break;
        }

        Console.WriteLine($"< {json}");

        await ProcessEnvelopeAsync(json, socket, sessionId, password, webRtc, serializerOptions, false);
    }
}

static async Task CommandLoopAsync(ClientWebSocket socket, WebRtcOperator webRtc, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
        var line = Console.ReadLine();
        if (line is null)
        {
            break;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            continue;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "monitor_switch" when parts.Length >= 2:
                await SendControlAsync(socket, webRtc, new MonitorSwitch(parts[1]), serializerOptions);
                break;
            case "mouse" when parts.Length >= 3 && double.TryParse(parts[1], out var x) && double.TryParse(parts[2], out var y):
                await SendControlAsync(socket, webRtc, new InputMessage(new MousePayload(x, y, null, null), null), serializerOptions);
                break;
            case "click" when parts.Length >= 2:
                var button = parts[1].ToLowerInvariant();
                var buttons = button switch
                {
                    "left" => new MouseButtons(true, null, null),
                    "right" => new MouseButtons(null, true, null),
                    "middle" => new MouseButtons(null, null, true),
                    _ => null
                };

                if (buttons is not null)
                {
                    await SendControlAsync(socket, webRtc, new InputMessage(new MousePayload(null, null, null, buttons), null), serializerOptions);
                    await SendControlAsync(socket, webRtc, new InputMessage(new MousePayload(null, null, null, new MouseButtons(false, false, false)), null), serializerOptions);
                }
                break;
            case "wheel" when parts.Length >= 2 && double.TryParse(parts[1], out var delta):
                await SendControlAsync(socket, webRtc, new InputMessage(new MousePayload(null, null, delta, null), null), serializerOptions);
                break;
            case "key" when parts.Length >= 3 && int.TryParse(parts[1], out var scanCode):
                var isDown = parts[2].Equals("down", StringComparison.OrdinalIgnoreCase);
                await SendControlAsync(socket, webRtc, new InputMessage(null, new KeyboardPayload(scanCode, isDown)), serializerOptions);
                break;
            default:
                Console.WriteLine("Unknown command");
                break;
        }
    }
}

static async Task ProcessEnvelopeAsync(string json, ClientWebSocket socket, string sessionId, string password, WebRtcOperator webRtc, JsonSerializerOptions serializerOptions, bool fromDataChannel)
{
    using var doc = JsonDocument.Parse(json);
    if (!doc.RootElement.TryGetProperty("type", out var typeElement))
    {
        return;
    }

    var type = typeElement.GetString();
    switch (type)
    {
        case SignalingMessageTypes.HostHello:
            Console.WriteLine("Host responded; requesting monitor list and authenticating...");
            await SendControlAsync(socket, webRtc, new MonitorListRequest(sessionId), serializerOptions);
            if (!string.IsNullOrWhiteSpace(password))
            {
                await SendControlAsync(socket, webRtc, new AuthRequest(password), serializerOptions);
            }
            break;
        case MessageTypes.MonitorList:
            Console.WriteLine("Monitors updated; send monitor_switch <id> to change.");
            break;
        case MessageTypes.AuthResult:
            Console.WriteLine("Authentication result received.");
            if (doc.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ok")
            {
                Console.WriteLine("Starting to receive frames... saved under ./frames");
            }
            break;
        case MessageTypes.Frame:
            await SaveFrameAsync(doc.RootElement);
            break;
        case SignalingMessageTypes.SdpOffer:
            {
                var offer = JsonSerializer.Deserialize<SdpOffer>(json, serializerOptions);
                if (offer is not null)
                {
                    await webRtc.HandleOfferAsync(offer);
                }

                break;
            }
        case SignalingMessageTypes.IceCandidate:
            {
                var candidate = JsonSerializer.Deserialize<IceCandidate>(json, serializerOptions);
                if (candidate is not null)
                {
                    await webRtc.AddRemoteCandidateAsync(candidate);
                }

                break;
            }
    }

    if (type == "command" && doc.RootElement.TryGetProperty("value", out var value))
    {
        var text = value.GetString();
        if (string.Equals(text, "switch", StringComparison.OrdinalIgnoreCase))
        {
            Console.Write("Enter monitor id: ");
            var monitorId = Console.ReadLine() ?? string.Empty;
            await SendControlAsync(socket, webRtc, new MonitorSwitch(monitorId), serializerOptions);
        }
    }

    if (fromDataChannel)
    {
        Console.WriteLine("[webrtc] handled control message via data channel");
    }
}

static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
{
    var buffer = new byte[16 * 1024];
    var builder = new MemoryStream();

    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }

        await builder.WriteAsync(buffer.AsMemory(0, result.Count));
        if (result.EndOfMessage)
        {
            return Encoding.UTF8.GetString(builder.GetBuffer(), 0, (int)builder.Length);
        }
    }
}

static async Task SaveFrameAsync(JsonElement element)
{
    if (!element.TryGetProperty("data", out var dataElement))
    {
        return;
    }

    var fileName = $"frame-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png";
    var directory = Path.Combine(Environment.CurrentDirectory, "frames");
    Directory.CreateDirectory(directory);

    var bytes = Convert.FromBase64String(dataElement.GetString() ?? string.Empty);
    var path = Path.Combine(directory, fileName);
    await File.WriteAllBytesAsync(path, bytes);

    var width = element.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
    var height = element.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;
    Console.WriteLine($"Saved frame to {path} ({width}x{height})");
}

static async Task SaveBinaryFrameAsync(FrameBinaryHeader header, byte[] payload)
{
    var fileName = $"frame-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-rtc.png";
    var directory = Path.Combine(Environment.CurrentDirectory, "frames");
    Directory.CreateDirectory(directory);

    var path = Path.Combine(directory, fileName);
    await File.WriteAllBytesAsync(path, payload);

    Console.WriteLine($"Saved frame over WebRTC to {path} ({header.Width}x{header.Height})");
}

static Task SendAsync<T>(ClientWebSocket socket, T message, JsonSerializerOptions serializerOptions)
{
    var json = JsonSerializer.Serialize(message, serializerOptions);

    Console.WriteLine($"> {json}");
    return socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
}

static async Task SendControlAsync(ClientWebSocket socket, WebRtcOperator webRtc, IDataChannelMessage message, JsonSerializerOptions serializerOptions)
{
    if (await webRtc.TrySendControlMessageAsync(message).ConfigureAwait(false))
    {
        var json = JsonSerializer.Serialize(message, serializerOptions);
        Console.WriteLine($"> [dc] {json}");
        return;
    }

    await SendAsync(socket, message, serializerOptions).ConfigureAwait(false);
}
