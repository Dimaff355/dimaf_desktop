using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using RemoteDesktop.Shared.Messaging;

namespace Configurator;

public partial class MainWindow : Window
{
    private const string PipeName = "P2PRD.Config";
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
    }

    private async void OnRefreshStatus(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
    }

    private async void OnSavePassword(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            SetStatus("Enter a non-empty password.");
            return;
        }

        var request = new IpcSetPasswordRequest { Password = password };
        var result = await SendAsync<IpcCommandResult>(request).ConfigureAwait(true);
        if (result is null)
        {
            SetStatus("Failed to save password: no response from service.");
            return;
        }

        SetStatus(result.Status == "ok" ? "Password updated." : $"Password update failed: {result.Error}");
        PasswordBox.Password = string.Empty;
        await RefreshStatusAsync();
    }

    private async void OnSaveResolver(object sender, RoutedEventArgs e)
    {
        var resolver = ResolverBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(resolver))
        {
            SetStatus("Enter a resolver URL.");
            return;
        }

        var request = new IpcSetResolverRequest { ResolverUrl = resolver };
        var result = await SendAsync<IpcCommandResult>(request).ConfigureAwait(true);
        if (result is null)
        {
            SetStatus("Failed to save resolver: no response from service.");
            return;
        }

        SetStatus(result.Status == "ok" ? "Resolver updated." : $"Resolver update failed: {result.Error}");
        await RefreshStatusAsync();
    }

    private async void OnSaveIce(object sender, RoutedEventArgs e)
    {
        var stun = StunBox.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        var turnUrl = TurnUrlBox.Text.Trim();
        var turnUsername = TurnUsernameBox.Text.Trim();
        var turnCredential = TurnCredentialBox.Password.Trim();

        if (stun.Length == 0 && string.IsNullOrWhiteSpace(turnUrl))
        {
            SetStatus("Enter at least one STUN server or a TURN URL.");
            return;
        }

        var request = new IpcSetIceRequest
        {
            StunServers = stun,
            TurnUrl = turnUrl,
            TurnUsername = turnUsername,
            TurnCredential = turnCredential
        };

        var result = await SendAsync<IpcCommandResult>(request).ConfigureAwait(true);
        if (result is null)
        {
            SetStatus("Failed to save ICE settings: no response from service.");
            return;
        }

        SetStatus(result.Status == "ok" ? "ICE settings updated." : $"ICE update failed: {result.Error}");
        await RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var status = await SendAsync<IpcStatusResponse>(new IpcStatusRequest()).ConfigureAwait(true);
        if (status is null)
        {
            SetStatus("Service unavailable. Ensure the host service is running as SYSTEM/Administrator.");
            return;
        }

        HostIdText.Text = status.HostId.ToString();
        PasswordStatusText.Text = status.HasPassword ? "configured" : "not set";
        ResolverText.Text = string.IsNullOrWhiteSpace(status.SignalingResolverUrl)
            ? "(not set)"
            : status.SignalingResolverUrl;
        ResolverBox.Text = status.SignalingResolverUrl;
        StunBox.Text = string.Join(Environment.NewLine, status.StunServers);
        TurnUrlBox.Text = status.Turn.Url;
        TurnUsernameBox.Text = status.Turn.Username;
        TurnCredentialBox.Password = status.Turn.Credential;
        SetStatus("Status refreshed.");
    }

    private async Task<T?> SendAsync<T>(object payload)
    {
        if (!OperatingSystem.IsWindows())
        {
            SetStatus("Configurator requires Windows to reach the named pipe.");
            return default;
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(2000).ConfigureAwait(false);

            await using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(payload, _jsonOptions)).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(line, _jsonOptions);
        }
        catch (Exception ex)
        {
            SetStatus($"IPC error: {ex.Message}");
            return default;
        }
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}
