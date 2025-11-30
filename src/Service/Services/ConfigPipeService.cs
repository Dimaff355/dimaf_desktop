using System.IO.Pipes;
using System.IO.Pipes.AccessControl;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Config;
using RemoteDesktop.Shared.Messaging;
using RemoteDesktop.Shared.Security;

namespace RemoteDesktop.Service.Services;

public sealed class ConfigPipeService : BackgroundService
{
    private const string PipeName = "P2PRD.Config";
    private readonly ILogger<ConfigPipeService> _logger;
    private readonly HostConfigProvider _configProvider;
    private readonly IPasswordHasher _passwordHasher;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ConfigPipeService(
        ILogger<ConfigPipeService> logger,
        HostConfigProvider configProvider,
        IPasswordHasher passwordHasher)
    {
        _logger = logger;
        _configProvider = configProvider;
        _passwordHasher = passwordHasher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var pipe = CreatePipe();

            try
            {
                _logger.LogInformation("Config pipe listening on {PipeName}", PipeName);
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Config pipe client connected");

                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
                {
                    AutoFlush = true
                };

                while (!stoppingToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    await HandleAsync(line, writer, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Config pipe failed");
            }
        }
    }

    private async Task HandleAsync(string line, StreamWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("type", out var typeElement))
            {
                await WriteAsync(new IpcCommandResult { Status = "error", Error = "missing_type" }, writer, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var type = typeElement.GetString();
            switch (type)
            {
                case IpcMessageTypes.Status:
                    await WriteAsync(await BuildStatusAsync(cancellationToken).ConfigureAwait(false), writer, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case IpcMessageTypes.SetPassword:
                    await HandlePasswordAsync(doc.RootElement, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case IpcMessageTypes.SetResolver:
                    await HandleResolverAsync(doc.RootElement, writer, cancellationToken).ConfigureAwait(false);
                    break;
                case IpcMessageTypes.SetIce:
                    await HandleIceAsync(doc.RootElement, writer, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await WriteAsync(new IpcCommandResult { Status = "error", Error = "unknown_type" }, writer, cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Config pipe message failed");
            await WriteAsync(new IpcCommandResult { Status = "error", Error = "exception" }, writer, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HandlePasswordAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var password = root.TryGetProperty("password", out var passwordElement)
            ? passwordElement.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(password))
        {
            await WriteAsync(new IpcCommandResult { Status = "error", Error = "empty_password" }, writer, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var config = await _configProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var hashed = _passwordHasher.Hash(password);
        var updated = config with
        {
            PasswordHash = hashed,
            Lockout = new LockoutConfig()
        };

        await _configProvider.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        await WriteAsync(new IpcCommandResult { Status = "ok" }, writer, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleResolverAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var resolver = root.TryGetProperty("resolver_url", out var resolverElement)
            ? resolverElement.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(resolver))
        {
            await WriteAsync(new IpcCommandResult { Status = "error", Error = "empty_resolver" }, writer, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var config = await _configProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var updated = config with
        {
            SignalingResolverUrl = resolver
        };

        await _configProvider.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        await WriteAsync(new IpcCommandResult { Status = "ok" }, writer, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleIceAsync(JsonElement root, StreamWriter writer, CancellationToken cancellationToken)
    {
        var stunServers = root.TryGetProperty("stun", out var stunElement)
            ? stunElement.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray()
            : Array.Empty<string>();

        var turnUrl = root.TryGetProperty("turn_url", out var turnUrlElement)
            ? turnUrlElement.GetString() ?? string.Empty
            : string.Empty;

        var turnUsername = root.TryGetProperty("turn_username", out var turnUsernameElement)
            ? turnUsernameElement.GetString() ?? string.Empty
            : string.Empty;

        var turnCredential = root.TryGetProperty("turn_credential", out var turnCredentialElement)
            ? turnCredentialElement.GetString() ?? string.Empty
            : string.Empty;

        if (stunServers.Length == 0 && string.IsNullOrWhiteSpace(turnUrl))
        {
            await WriteAsync(new IpcCommandResult { Status = "error", Error = "empty_ice" }, writer, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var config = await _configProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var updated = config with
        {
            StunServers = stunServers.Length > 0 ? stunServers : config.StunServers,
            Turn = new TurnConfig
            {
                Url = turnUrl,
                Username = turnUsername,
                Credential = turnCredential
            }
        };

        await _configProvider.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        await WriteAsync(new IpcCommandResult { Status = "ok" }, writer, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IpcStatusResponse> BuildStatusAsync(CancellationToken cancellationToken)
    {
        var config = await _configProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        return new IpcStatusResponse
        {
            HostId = config.HostId,
            HasPassword = !string.IsNullOrEmpty(config.PasswordHash),
            SignalingResolverUrl = config.SignalingResolverUrl,
            StunServers = config.StunServers,
            Turn = config.Turn
        };
    }

    private async Task WriteAsync<T>(T payload, StreamWriter writer, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, _serializerOptions);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private NamedPipeServerStream CreatePipe()
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity: security,
                inheritability: HandleInheritability.None);
        }

        return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
    }
}
