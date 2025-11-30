using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Service.Services;

/// <summary>
/// Switches the current thread onto the active input desktop so capture and input
/// can see secure desktops (UAC/logon) when permitted. No-op on non-Windows hosts
/// and best-effort otherwise to avoid destabilizing the service thread.
/// </summary>
public sealed class InputDesktopSwitcher
{
    private readonly ILogger _logger;

    public InputDesktopSwitcher(ILogger<InputDesktopSwitcher> logger)
    {
        _logger = logger;
    }

    public IDisposable? TryEnterInputDesktop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var original = GetThreadDesktop(GetCurrentThreadId());
        if (original == IntPtr.Zero)
        {
            _logger.LogDebug("GetThreadDesktop returned null; skipping desktop switch");
            return null;
        }

        var input = OpenInputDesktop(0, false, DesiredAccess);
        if (input == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogDebug("OpenInputDesktop failed with error {Error}; continuing on current desktop", error);
            return null;
        }

        if (input == original)
        {
            return new NoopDesktopScope(input);
        }

        if (!SetThreadDesktop(input))
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogDebug("SetThreadDesktop failed with error {Error}; continuing on current desktop", error);
            CloseDesktop(input);
            return null;
        }

        _logger.LogDebug("Switched thread to input desktop");
        return new DesktopScope(_logger, original, input);
    }

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_CREATEWINDOW = 0x0002;
    private const uint DESKTOP_CREATEMENU = 0x0004;
    private const uint DESKTOP_HOOKCONTROL = 0x0008;
    private const uint DESKTOP_WRITEOBJECTS = 0x0080;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const uint DesiredAccess = DESKTOP_READOBJECTS | DESKTOP_CREATEWINDOW | DESKTOP_CREATEMENU | DESKTOP_HOOKCONTROL | DESKTOP_WRITEOBJECTS | DESKTOP_SWITCHDESKTOP;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(int dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    private sealed class NoopDesktopScope : IDisposable
    {
        private readonly IntPtr _desktop;

        public NoopDesktopScope(IntPtr desktop)
        {
            _desktop = desktop;
        }

        public void Dispose()
        {
            CloseDesktop(_desktop);
        }
    }

    private sealed class DesktopScope : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IntPtr _original;
        private readonly IntPtr _input;

        public DesktopScope(ILogger logger, IntPtr original, IntPtr input)
        {
            _logger = logger;
            _original = original;
            _input = input;
        }

        public void Dispose()
        {
            try
            {
                if (!SetThreadDesktop(_original))
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger.LogDebug("Failed to restore original desktop (error {Error})", error);
                }
            }
            finally
            {
                CloseDesktop(_input);
            }
        }
    }
}
