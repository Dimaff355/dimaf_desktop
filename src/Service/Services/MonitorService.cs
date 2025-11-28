using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Models;

namespace RemoteDesktop.Service.Services;

public sealed class MonitorService
{
    private readonly ILogger<MonitorService> _logger;

    public MonitorService(ILogger<MonitorService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<MonitorDescriptor> Enumerate()
    {
        // Windows-specific enumeration (DXGI / WMI) is planned. For now we expose a
        // deterministic stub that keeps the rest of the pipeline working end-to-end
        // on any platform, including CI containers.
        var descriptors = new List<MonitorDescriptor>
        {
            new("primary", "Primary", 1920, 1080, 1.0)
        };

        _logger.LogInformation("Enumerated {Count} monitor(s)", descriptors.Count);
        return descriptors;
    }
}
