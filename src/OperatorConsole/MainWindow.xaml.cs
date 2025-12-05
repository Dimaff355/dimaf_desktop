using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteDesktop.Shared.Messaging;
using RemoteDesktop.Shared.Models;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions.V1;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.Windows;

namespace OperatorConsole
{
    public partial class MainWindow : Window
    {
        private ClientWebSocket _socket;
        private WebRtcOperator _webRtc;
        private CancellationTokenSource _cts;
        private string _sessionId;
        private JsonSerializerOptions _serializerOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private WriteableBitmap _bitmap;
        private int _videoWidth, _videoHeight;
        private object _bitmapLock = new object();

        private ObservableCollection<MonitorDescriptor> _monitors = new ObservableCollection<MonitorDescriptor>();

        public MainWindow()
        {
            InitializeComponent();
            cmbMonitors.ItemsSource = _monitors;
            cmbMonitors.DisplayMemberPath = "Name";
            cmbMonitors.SelectedValuePath = "Id";

            // For testing: set default values
            txtSignalingUrl.Text = "ws://localhost:5000/ws";
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            string url = txtSignalingUrl.Text.Trim();
            string hostIdStr = txtHostId.Text.Trim();
            string password = txtPassword.Password;

            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(hostIdStr))
            {
                MessageBox.Show("Please enter Signaling URL and Host ID");
                return;
            }

            if (!Guid.TryParse(hostIdStr, out Guid hostId))
            {
                MessageBox.Show("Host ID must be a valid GUID");
                return;
            }

            txtStatus.Text = "Connecting...";
            btnConnect.IsEnabled = false;
            btnDisconnect.IsEnabled = true;
            cmbMonitors.IsEnabled = false;
            btnCAD.IsEnabled = false;

            try
            {
                _sessionId = Guid.NewGuid().ToString();
                _socket = new ClientWebSocket();
                await _socket.ConnectAsync(new UriBuilder(url) { Query = $"role=operator&hostId={hostId}" }.Uri, CancellationToken.None);
                txtStatus.Text = "Connected, establishing WebRTC...";

                _cts = new CancellationTokenSource();

                // Initialize WebRTC
                _webRtc = new WebRtcOperator(
                    new[] { "stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302" },
                    SendSignalingMessageAsync,
                    ReceiveSignalingMessageAsync,
                    OnBinaryFrame,
                    OnVideoFrame);

                await SendControlAsync(new OperatorHello(_sessionId));

                // Start receive loop
                _ = ReceiveLoopAsync(_cts.Token);

                // Request monitor list
                await SendControlAsync(new MonitorListRequest(_sessionId));
                if (!string.IsNullOrEmpty(password))
                {
                    await SendControlAsync(new AuthRequest(password));
                }

                // We'll start video when we receive SDP etc. automatically via WebRTC.
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connection error: {ex.Message}");
                Disconnect();
            }
        }

        private async void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
            txtStatus.Text = "Disconnected";
        }

        private void Disconnect()
        {
            _cts?.Cancel();
            _webRtc?.DisposeAsync().AsTask().Wait(); // careful: wait but should be quick
            _webRtc = null;
            try
            {
                _socket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None).Wait();
            }
            catch { }
            _socket = null;
            btnConnect.IsEnabled = true;
            btnDisconnect.IsEnabled = false;
            cmbMonitors.IsEnabled = false;
            btnCAD.IsEnabled = false;
            _monitors.Clear();
            imgVideo.Source = null;
        }

        private async Task SendSignalingMessageAsync(IDataChannelMessage message)
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
                return;
            var json = JsonSerializer.Serialize(message, _serializerOptions);
            await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private async Task SendControlAsync(IDataChannelMessage message)
        {
            if (_webRtc != null && await _webRtc.TrySendControlMessageAsync(message))
            {
                // sent via data channel
                return;
            }
            // fallback to signaling websocket
            await SendSignalingMessageAsync(message);
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (_socket != null && _socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                try
                {
                    var json = await ReceiveTextAsync(token);
                    if (json == null)
                        break;
                    await ProcessEnvelopeAsync(json, false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => txtStatus.Text = $"Error: {ex.Message}");
                    break;
                }
            }
            Dispatcher.Invoke(() => Disconnect());
        }

        private async Task<string> ReceiveTextAsync(CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            var builder = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                await builder.WriteAsync(buffer.AsMemory(0, result.Count), token);
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(builder.GetBuffer(), 0, (int)builder.Length);
            }
        }

        private async Task ProcessEnvelopeAsync(string json, bool fromDataChannel)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeElement))
                return;
            var type = typeElement.GetString();
            switch (type)
            {
                case SignalingMessageTypes.HostHello:
                    // Host responded, monitor list will come separately? Actually HostHello contains monitors.
                    // But we already request monitors later; maybe we can extract from HostHello.
                    // We'll rely on MonitorList message.
                    break;
                case MessageTypes.MonitorList:
                    var monitorList = JsonSerializer.Deserialize<MonitorList>(json, _serializerOptions);
                    if (monitorList != null)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _monitors.Clear();
                            foreach (var monitor in monitorList.Monitors)
                                _monitors.Add(monitor);
                            cmbMonitors.IsEnabled = true;
                            // select active?
                            if (!string.IsNullOrEmpty(monitorList.ActiveMonitorId))
                                cmbMonitors.SelectedValue = monitorList.ActiveMonitorId;
                            txtStatus.Text = "Monitors updated";
                        });
                    }
                    break;
                case MessageTypes.AuthResult:
                    var authResult = JsonSerializer.Deserialize<AuthResult>(json, _serializerOptions);
                    if (authResult != null)
                    {
                        Dispatcher.Invoke(() => txtStatus.Text = $"Authentication: {authResult.Status}");
                    }
                    break;
                case SignalingMessageTypes.SdpOffer:
                    var offer = JsonSerializer.Deserialize<SdpOffer>(json, _serializerOptions);
                    if (offer != null && _webRtc != null)
                    {
                        await _webRtc.HandleOfferAsync(offer);
                    }
                    break;
                case SignalingMessageTypes.IceCandidate:
                    var candidate = JsonSerializer.Deserialize<IceCandidate>(json, _serializerOptions);
                    if (candidate != null && _webRtc != null)
                    {
                        await _webRtc.AddRemoteCandidateAsync(candidate);
                    }
                    break;
                // Ignore others
            }

            if (fromDataChannel)
            {
                // Could log
            }
        }

        private async Task ReceiveSignalingMessageAsync(string json)
        {
            await ProcessEnvelopeAsync(json, true);
        }

        private void OnBinaryFrame(FrameBinaryHeader header, byte[] payload)
        {
            // Could handle binary frames (e.g., raw PNG) but we use video frames.
        }

        private void OnVideoFrame(RawImage frame)
        {
            if (frame.PixelFormat != VideoPixelFormatsEnum.Bgra || frame.Width == 0 || frame.Height == 0)
                return;

            Dispatcher.Invoke(() =>
            {
                lock (_bitmapLock)
                {
                    if (_bitmap == null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
                    {
                        _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                        imgVideo.Source = _bitmap;
                        _videoWidth = frame.Width;
                        _videoHeight = frame.Height;
                    }
                    _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Data, frame.Stride, 0);
                }
            });
        }

        // UI event handlers for input

        private void ImgVideo_MouseMove(object sender, MouseEventArgs e)
        {
            if (_webRtc == null || _socket == null) return;
            var pos = e.GetPosition(imgVideo);
            if (imgVideo.ActualWidth <= 0 || imgVideo.ActualHeight <= 0) return;
            double relX = pos.X / imgVideo.ActualWidth;
            double relY = pos.Y / imgVideo.ActualHeight;
            if (relX < 0 || relX > 1 || relY < 0 || relY > 1) return;
            _ = SendControlAsync(new InputMessage(new MousePayload(relX, relY, null, null, null), null));
        }

        private void ImgVideo_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_webRtc == null) return;
            var button = e.ChangedButton;
            MouseButtons? buttons = null;
            if (button == MouseButton.Left)
                buttons = new MouseButtons(true, null, null, null, null);
            else if (button == MouseButton.Right)
                buttons = new MouseButtons(null, true, null, null, null);
            else if (button == MouseButton.Middle)
                buttons = new MouseButtons(null, null, true, null, null);
            if (buttons != null)
                _ = SendControlAsync(new InputMessage(new MousePayload(null, null, null, null, buttons), null));
        }

        private void ImgVideo_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_webRtc == null) return;
            var button = e.ChangedButton;
            MouseButtons? buttons = null;
            if (button == MouseButton.Left)
                buttons = new MouseButtons(false, null, null, null, null);
            else if (button == MouseButton.Right)
                buttons = new MouseButtons(null, false, null, null, null);
            else if (button == MouseButton.Middle)
                buttons = new MouseButtons(null, null, false, null, null);
            if (buttons != null)
                _ = SendControlAsync(new InputMessage(new MousePayload(null, null, null, null, buttons), null));
        }

        private void ImgVideo_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_webRtc == null) return;
            double delta = e.Delta > 0 ? 1 : -1;
            _ = SendControlAsync(new InputMessage(new MousePayload(null, null, delta, null, null), null));
        }

        private void BtnCAD_Click(object sender, RoutedEventArgs e)
        {
            if (_webRtc == null) return;
            _ = SendControlAsync(new InputMessage(null, null, new SpecialPayload("ctrl_alt_del")));
        }

        private void CmbMonitors_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_webRtc == null || cmbMonitors.SelectedValue == null) return;
            var id = cmbMonitors.SelectedValue.ToString();
            _ = SendControlAsync(new MonitorSwitch(id));
        }

        // Window closing cleanup
        private void Window_Closed(object sender, EventArgs e)
        {
            Disconnect();
        }
    }
}