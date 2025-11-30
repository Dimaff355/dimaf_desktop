using Microsoft.Extensions.Logging;
using RemoteDesktop.Shared.Models;
using System.Drawing;
using System.Runtime.InteropServices;
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
                scale: GetDpiScale(s)))
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

    public MonitorDescriptor? GetDescriptor(string? monitorId)
    {
        if (_monitors.Count == 0)
        {
            _ = Enumerate();
        }

        return _monitors.FirstOrDefault(m => m.Id.Equals(monitorId, StringComparison.OrdinalIgnoreCase))
            ?? _monitors.FirstOrDefault();
    }

    private double GetDpiScale(Screen screen)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1.0;
        }

        try
        {
            var point = new POINT { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 };
            var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var hr = GetDpiForMonitor(monitor, MonitorDpiType.EffectiveDpi, out uint dpiX, out uint dpiY);
                if (hr == 0 && dpiX > 0)
                {
                    return dpiX / 96.0;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query DPI for monitor {Monitor}", screen.DeviceName);
        }

        return 1.0;
    }

    #region Win32 DPI

    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    private enum MonitorDpiType
    {
        EffectiveDpi = 0,
        AngularDpi = 1,
        RawDpi = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("User32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    #endregion
}
