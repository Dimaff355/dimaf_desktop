using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

var rateLimiter = new SlidingRateLimiter(maxRequestsPerWindow: 10, window: TimeSpan.FromSeconds(1));
var hosts = new ConcurrentDictionary<Guid, WebSocketConnection>();
var operators = new ConcurrentDictionary<Guid, ConcurrentBag<WebSocketConnection>>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!rateLimiter.Allow(remoteIp))
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.Response.WriteAsync("rate limit");
        return;
    }

    var role = context.Request.Query["role"].ToString();
    var hostIdQuery = context.Request.Query["hostId"].ToString();
    Guid.TryParse(hostIdQuery, out var hostId);

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connection = new WebSocketConnection(socket);

    if (string.Equals(role, "operator", StringComparison.OrdinalIgnoreCase))
    {
        if (hostId == Guid.Empty)
        {
            await connection.CloseAsync("missing hostId");
            return;
        }

        await connection.SendAsync("{\"type\":\"welcome\",\"role\":\"operator\"}");
        var list = operators.GetOrAdd(hostId, _ => new());
        list.Add(connection);

        await PumpAsync(connection, payload => SendToHostAsync(hostId, payload), () => RemoveOperator(hostId, connection));
        return;
    }

    // Default to host role when unspecified.
    await connection.SendAsync("{\"type\":\"welcome\",\"role\":\"host\"}");

    await PumpAsync(connection,
        async payload =>
        {
            var knownHostId = hostId;
            if (knownHostId == Guid.Empty)
            {
                knownHostId = TryExtractHostId(payload);
                if (knownHostId != Guid.Empty)
                {
                    hostId = knownHostId;
                    hosts[knownHostId] = connection;
                }
            }

            if (knownHostId != Guid.Empty && operators.TryGetValue(knownHostId, out var listeners))
            {
                foreach (var op in listeners)
                {
                    await op.SendAsync(payload);
                }
            }
        },
        () =>
        {
            if (hostId != Guid.Empty)
            {
                hosts.TryRemove(hostId, out _);
                operators.TryRemove(hostId, out _);
            }
        });
});

app.Run();

async Task PumpAsync(WebSocketConnection connection, Func<string, Task> onPayload, Action onClose)
{
    try
    {
        await foreach (var payload in connection.ReadAsync())
        {
            await onPayload(payload);
        }
    }
    finally
    {
        onClose();
        await connection.CloseAsync("closed");
    }
}

async Task SendToHostAsync(Guid hostId, string payload)
{
    if (hostId == Guid.Empty)
    {
        return;
    }

    if (_ = hosts.TryGetValue(hostId, out var target))
    {
        await target.SendAsync(payload);
    }
}

void RemoveOperator(Guid hostId, WebSocketConnection connection)
{
    if (hostId == Guid.Empty)
    {
        return;
    }

    if (operators.TryGetValue(hostId, out var bag))
    {
        var survivors = new ConcurrentBag<WebSocketConnection>(bag.Where(c => c != connection));
        operators[hostId] = survivors;
    }
}

Guid TryExtractHostId(string payload)
{
    try
    {
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("host_id", out var idElement) &&
            Guid.TryParse(idElement.GetString(), out var hostId))
        {
            return hostId;
        }
    }
    catch (JsonException)
    {
        // Non-JSON payloads should not break the loop; they just don't provide host identity info.
    }

    return Guid.Empty;
}

internal sealed class WebSocketConnection
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public WebSocketConnection(WebSocket socket)
    {
        _socket = socket;
    }

    public async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                yield break;
            }

            using var payloadStream = new MemoryStream();
            await payloadStream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);

            while (!result.EndOfMessage && !cancellationToken.IsCancellationRequested)
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken);
                await payloadStream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            }

            var text = Encoding.UTF8.GetString(payloadStream.GetBuffer(), 0, (int)payloadStream.Length);
            yield return text;
        }
    }

    public async Task SendAsync(string payload, CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public Task CloseAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (_socket.State == WebSocketState.Open)
        {
            return _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken);
        }

        return Task.CompletedTask;
    }
}

internal sealed class SlidingRateLimiter
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new();
    private readonly int _maxRequestsPerWindow;
    private readonly TimeSpan _window;

    public SlidingRateLimiter(int maxRequestsPerWindow, TimeSpan window)
    {
        _maxRequestsPerWindow = maxRequestsPerWindow;
        _window = window;
    }

    public bool Allow(string key)
    {
        var now = DateTimeOffset.UtcNow;
        var counter = _counters.GetOrAdd(key, _ => new Counter(now, 0));

        lock (counter)
        {
            if (now - counter.WindowStart >= _window)
            {
                counter.WindowStart = now;
                counter.Count = 0;
            }

            counter.Count++;
            return counter.Count <= _maxRequestsPerWindow;
        }
    }

    private sealed class Counter
    {
        public Counter(DateTimeOffset windowStart, int count)
        {
            WindowStart = windowStart;
            Count = count;
        }

        public DateTimeOffset WindowStart { get; set; }

        public int Count { get; set; }
    }
}
