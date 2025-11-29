using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Config;

namespace RemoteDesktop.Service.Services;

public sealed class HostConfigProvider
{
    private readonly ILogger<HostConfigProvider> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private HostConfig? _cached;
    private readonly string _configPath;
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public HostConfigProvider(ILogger<HostConfigProvider> logger)
    {
        _logger = logger;
        _configPath = GetDefaultPath();
    }

    public async ValueTask<HostConfig> GetAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            _cached = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SaveAsync(HostConfig config, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            await using var stream = File.Create(_configPath);
            await JsonSerializer.SerializeAsync(stream, config, _options, cancellationToken).ConfigureAwait(false);
            _cached = config;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<HostConfig> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configPath))
        {
            _logger.LogWarning("Host configuration missing, creating default at {ConfigPath}", _configPath);
            var defaultConfig = HostConfig.CreateDefault();
            await SaveAsync(defaultConfig, cancellationToken).ConfigureAwait(false);
            return defaultConfig;
        }

        await using var stream = File.OpenRead(_configPath);
        var config = await JsonSerializer.DeserializeAsync<HostConfig>(stream, _options, cancellationToken)
            .ConfigureAwait(false);

        if (config is null)
        {
            _logger.LogWarning("Host configuration corrupted; regenerating defaults");
            var defaultConfig = HostConfig.CreateDefault();
            await SaveAsync(defaultConfig, cancellationToken).ConfigureAwait(false);
            return defaultConfig;
        }

        return config;
    }

    private static string GetDefaultPath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "P2PRD", "config.json");
    }
}
