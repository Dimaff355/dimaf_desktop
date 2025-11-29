using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Models;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteDesktop.Service.Services;

public sealed class MonitorService
{
    private readonly ILogger<MonitorService> _logger;

    public MonitorService(ILogger<MonitorService> logger)
    {
        _logger = logger;
    }

    private IReadOnlyList<MonitorDescriptor> _monitors = Array.Empty<MonitorDescriptor>();

    public IReadOnlyList<MonitorDescriptor> Enumerate()
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0)
        {
            _monitors = new[] { new MonitorDescriptor("primary", "Primary", 1920, 1080, 1.0) };
            return _monitors;
        }

        _monitors = screens
            .Select((s, i) => new MonitorDescriptor(
                id: s.DeviceName,
                name: s.DeviceName.Trim('\0'),
                width: s.Bounds.Width,
                height: s.Bounds.Height,
                scale: 1.0))
            .ToArray();

        _logger.LogInformation("Enumerated {Count} monitor(s)", _monitors.Count);
        return _monitors;
    }

    public string GetPrimaryMonitorId()
    {
        if (_monitors.Count == 0)
        {
            _ = Enumerate();
        }

        return _monitors.FirstOrDefault()?.Id ?? "primary";
    }

    public Rectangle? GetBounds(string? monitorId)
    {
        if (_monitors.Count == 0)
        {
            _ = Enumerate();
        }

        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName.Equals(monitorId, StringComparison.OrdinalIgnoreCase))
            ?? Screen.PrimaryScreen;

        return screen?.Bounds;
    }
}
