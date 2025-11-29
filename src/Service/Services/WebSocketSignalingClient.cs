using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Service.Services;

public sealed class WebSocketSignalingClient : IAsyncDisposable
{
    private readonly ILogger<WebSocketSignalingClient> _logger;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Func<string, CancellationToken, Task>? _onTextMessage;

    public event Action? Disconnected;

    public WebSocketSignalingClient(ILogger<WebSocketSignalingClient> logger)
    {
        _logger = logger;
    }

    public void SetMessageHandler(Func<string, CancellationToken, Task> handler)
    {
        _onTextMessage = handler;
    }

    public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        _socket = new ClientWebSocket();
        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _logger.LogInformation("Connecting to signaling server {Endpoint}", endpoint);
        await _socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    public async Task SendTextAsync(string payload, CancellationToken cancellationToken)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Signaling socket is not connected");
        }

        var buffer = Encoding.UTF8.GetBytes(payload);
        await _socket.SendAsync(buffer, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            return;
        }

        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                using var payloadStream = new MemoryStream();
                await payloadStream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);

                while (!result.EndOfMessage && !cancellationToken.IsCancellationRequested)
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    await payloadStream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
                }

                var text = Encoding.UTF8.GetString(payloadStream.GetBuffer(), 0, (int)payloadStream.Length);
                if (_onTextMessage is not null)
                {
                    try
                    {
                        await _onTextMessage(text, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Unhandled exception in signaling message handler");
                    }
                }
                else
                {
                    _logger.LogInformation("Received signaling payload: {Payload}", text);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Signaling socket faulted");
        }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_socket is null)
        {
            return;
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Error while closing signaling socket");
        }
        finally
        {
            _receiveCts?.Cancel();
            _socket.Dispose();
            _socket = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
