using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Service.Services;

public sealed record CapturedFrame(byte[] PngData, byte[] BgraData, int Width, int Height, string Format);

public sealed class CaptureService
{
    private readonly MonitorService _monitorService;
    private readonly ILogger<CaptureService> _logger;

    public CaptureService(MonitorService monitorService, ILogger<CaptureService> logger)
    {
        _monitorService = monitorService;
        _logger = logger;
    }

    public CapturedFrame Capture(string monitorId)
    {
        var bounds = _monitorService.GetBounds(monitorId);
        if (bounds is null)
        {
            _logger.LogWarning("No bounds available for monitor {MonitorId}; falling back to primary", monitorId);
            bounds = _monitorService.GetBounds(_monitorService.GetPrimaryMonitorId());
        }

        if (bounds is null)
        {
            return GenerateFallbackFrame();
        }

        using var bitmap = new Bitmap(bounds.Value.Width, bounds.Value.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Value.Left, bounds.Value.Top, 0, 0, bounds.Value.Size, CopyPixelOperation.SourceCopy);

        var bgraData = ExtractBgra(bitmap);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new CapturedFrame(stream.ToArray(), bgraData, bounds.Value.Width, bounds.Value.Height, "image/png");
    }

    private CapturedFrame GenerateFallbackFrame()
    {
        const int width = 640;
        const int height = 360;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.Clear(Color.MidnightBlue);
        var timestamp = DateTimeOffset.UtcNow.ToString("u");
        graphics.DrawString(
            "Capture unavailable\nSimulated frame\n" + timestamp,
            new Font("Segoe UI", 16, FontStyle.Bold),
            Brushes.White,
            new RectangleF(10, 10, width - 20, height - 20));

        var bgraData = ExtractBgra(bitmap);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new CapturedFrame(stream.ToArray(), bgraData, width, height, "image/png");
    }

    private static byte[] ExtractBgra(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            var length = data.Stride * data.Height;
            var buffer = new byte[length];
            Marshal.Copy(data.Scan0, buffer, 0, length);
            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
