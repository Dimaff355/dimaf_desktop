using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Service.Services;

public sealed class WebSocketSignalingClient : IAsyncDisposable
{
    private readonly ILogger<WebSocketSignalingClient> _logger;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;

    public WebSocketSignalingClient(ILogger<WebSocketSignalingClient> logger)
    {
        _logger = logger;
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

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _logger.LogInformation("Received signaling payload: {Payload}", text);
                // TODO: Dispatch signaling messages to the WebRTC stack.
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
