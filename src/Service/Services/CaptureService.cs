using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace RemoteDesktop.Service.Services;

public sealed record CapturedFrame(byte[] PngData, byte[] BgraData, int Width, int Height, string Format);

public sealed class CaptureService : IDisposable
{
    private readonly MonitorService _monitorService;
    private readonly ILogger<CaptureService> _logger;
    private readonly InputDesktopSwitcher _desktopSwitcher;
    private DxgiCaptureSession? _dxgiSession;
    private bool _dxgiAttempted;

    public CaptureService(MonitorService monitorService, InputDesktopSwitcher desktopSwitcher, ILogger<CaptureService> logger)
    {
        _monitorService = monitorService;
        _logger = logger;
        _desktopSwitcher = desktopSwitcher;
    }

    public CapturedFrame Capture(string monitorId)
    {
        using var desktopScope = _desktopSwitcher.TryEnterInputDesktop();

        if (OperatingSystem.IsWindows())
        {
            EnsureDxgiSession();
            if (_dxgiSession is not null && _dxgiSession.TryCapture(monitorId, out var frame))
            {
                return frame!;
            }
        }

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

    public void Dispose()
    {
        _dxgiSession?.Dispose();
    }

    private void EnsureDxgiSession()
    {
        if (_dxgiAttempted)
        {
            return;
        }

        _dxgiAttempted = true;

        try
        {
            _dxgiSession = DxgiCaptureSession.TryCreate(_monitorService, _logger);
            if (_dxgiSession is null)
            {
                _logger.LogWarning("DXGI capture unavailable; continuing with GDI fallback");
            }
            else
            {
                _logger.LogInformation("DXGI Desktop Duplication initialized for {Count} output(s)", _dxgiSession.MonitorCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DXGI capture initialization failed; falling back to GDI");
        }
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

    private sealed class DxgiCaptureSession : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly Dictionary<string, DuplicationContext> _duplications;

        private DxgiCaptureSession(ILogger logger, ID3D11Device device, ID3D11DeviceContext context, Dictionary<string, DuplicationContext> duplications)
        {
            _logger = logger;
            _device = device;
            _context = context;
            _duplications = duplications;
        }

        public int MonitorCount => _duplications.Count;

        public static DxgiCaptureSession? TryCreate(MonitorService monitorService, ILogger logger)
        {
            try
            {
                var result = D3D11.D3D11CreateDevice(
                    adapter: null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                    out ID3D11Device? device);

                if (result.Failure || device is null)
                {
                    logger.LogWarning("D3D11CreateDevice failed: {Result}", result.Code);
                    return null;
                }

                var context = device.ImmediateContext;
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                var monitors = monitorService.Enumerate();
                var duplications = new Dictionary<string, DuplicationContext>(StringComparer.OrdinalIgnoreCase);

                foreach (var adapter in factory.Adapters1)
                {
                    using (adapter)
                    {
                        foreach (var output in adapter.Outputs1)
                        {
                            using (output)
                            {
                                var description = output.Description1;
                                var rect = description.DesktopCoordinates;
                                var width = Math.Max(1, rect.Right - rect.Left);
                                var height = Math.Max(1, rect.Bottom - rect.Top);

                                var monitorMatch = monitors.FirstOrDefault(m =>
                                    m.Width == width && m.Height == height && description.DeviceName.Trim('\0').Contains(m.Name, StringComparison.OrdinalIgnoreCase));

                                var monitorId = monitorMatch?.Id ?? description.DeviceName.Trim('\0');

                                try
                                {
                                    var duplication = output.DuplicateOutput(device);
                                    var staging = device.CreateTexture2D(new Texture2DDescription
                                    {
                                        CpuAccessFlags = CpuAccessFlags.Read,
                                        BindFlags = BindFlags.None,
                                        Format = Format.B8G8R8A8_UNorm,
                                        Width = width,
                                        Height = height,
                                        MipLevels = 1,
                                        ArraySize = 1,
                                        SampleDescription = new SampleDescription(1, 0),
                                        Usage = ResourceUsage.Staging
                                    });

                                    duplications[monitorId] = new DuplicationContext(duplication, staging, width, height);
                                    logger.LogInformation("DXGI duplication ready for {Monitor} ({Width}x{Height})", monitorId, width, height);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogWarning(ex, "Failed to create duplication for {Monitor}", monitorId);
                                }
                            }
                        }
                    }
                }

                if (duplications.Count == 0)
                {
                    context.Dispose();
                    device.Dispose();
                    logger.LogWarning("No DXGI outputs available for duplication");
                    return null;
                }

                return new DxgiCaptureSession(logger, device, context, duplications);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "DXGI capture session creation failed");
                return null;
            }
        }

        public bool TryCapture(string monitorId, out CapturedFrame? frame)
        {
            frame = null;

            if (!_duplications.TryGetValue(monitorId, out var duplication) && _duplications.Count > 0)
            {
                duplication = _duplications.Values.First();
            }

            if (duplication is null)
            {
                return false;
            }

            try
            {
                var acquireResult = duplication.Duplication.AcquireNextFrame(10, out _, out var resource);
                if (acquireResult.Code == ResultCode.WaitTimeout.Code)
                {
                    return false;
                }

                if (acquireResult.Failure)
                {
                    _logger.LogDebug("AcquireNextFrame failed: {Result}", acquireResult.Code);
                    return false;
                }

                using (resource)
                using (var texture = resource.QueryInterface<ID3D11Texture2D>())
                {
                    _context.CopyResource(texture, duplication.Staging);
                }

                var width = duplication.Width;
                var height = duplication.Height;
                var bgra = new byte[width * height * 4];

                _context.Map(duplication.Staging, 0, MapMode.Read, MapFlags.None, out var map);
                try
                {
                    var sourcePtr = map.DataPointer;
                    var destinationOffset = 0;
                    for (var y = 0; y < height; y++)
                    {
                        var span = new ReadOnlySpan<byte>(sourcePtr + y * map.RowPitch, width * 4);
                        span.CopyTo(new Span<byte>(bgra, destinationOffset, width * 4));
                        destinationOffset += width * 4;
                    }
                }
                finally
                {
                    _context.Unmap(duplication.Staging, 0);
                    duplication.Duplication.ReleaseFrame();
                }

                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, width, height);
                var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (var y = 0; y < height; y++)
                    {
                        var destPtr = data.Scan0 + y * data.Stride;
                        Marshal.Copy(bgra, y * width * 4, destPtr, width * 4);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                frame = new CapturedFrame(stream.ToArray(), bgra, width, height, "image/png");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DXGI capture failed for {Monitor}", monitorId);
                return false;
            }
        }

        public void Dispose()
        {
            foreach (var duplication in _duplications.Values)
            {
                duplication.Dispose();
            }

            _context.Dispose();
            _device.Dispose();
        }

        private sealed class DuplicationContext : IDisposable
        {
            public DuplicationContext(IDXGIOutputDuplication duplication, ID3D11Texture2D staging, int width, int height)
            {
                Duplication = duplication;
                Staging = staging;
                Width = width;
                Height = height;
            }

            public IDXGIOutputDuplication Duplication { get; }
            public ID3D11Texture2D Staging { get; }
            public int Width { get; }
            public int Height { get; }

            public void Dispose()
            {
                Duplication.Dispose();
                Staging.Dispose();
            }
        }
    }
}
