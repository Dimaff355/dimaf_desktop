using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Service.Services;

public sealed class SignalingResolver
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SignalingResolver> _logger;

    public SignalingResolver(HttpClient httpClient, ILogger<SignalingResolver> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<Uri?> ResolveAsync(string resolverUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resolverUrl))
        {
            _logger.LogWarning("Signaling resolver URL is not configured");
            return null;
        }

        try
        {
            using var response = await _httpClient.GetAsync(resolverUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("url", out var urlElement) &&
                Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri))
            {
                return uri;
            }

            _logger.LogWarning("Resolver payload did not contain a valid 'url' field");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to resolve signaling endpoint via {ResolverUrl}", resolverUrl);
        }

        return null;
    }
}
