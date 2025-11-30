using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteDesktop.Service.Services;

public sealed class SessionSwitchService : BackgroundService
{
    private readonly ILogger<SessionSwitchService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private uint? _lastSessionId;

    public SessionSwitchService(ILogger<SessionSwitchService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("Session switch watcher is only available on Windows; skipping");
            return;
        }

        _logger.LogInformation("Starting active console session watcher");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sessionId = WTSGetActiveConsoleSessionId();
                if (_lastSessionId is null || sessionId != _lastSessionId)
                {
                    var state = DescribeState(sessionId);
                    _logger.LogInformation("Active console session changed: {SessionId} ({State})", sessionId, state);
                    _lastSessionId = sessionId;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session switch poll failed");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Stopping active console session watcher");
    }

    private string DescribeState(uint sessionId)
    {
        if (sessionId == 0xFFFFFFFF)
        {
            return "NoActiveConsole";
        }

        if (!WTSQuerySessionInformation(IntPtr.Zero, (int)sessionId, WTS_INFO_CLASS.WTSConnectState, out var buffer, out var bytesReturned) || buffer == IntPtr.Zero)
        {
            return "Unknown";
        }

        try
        {
            if (bytesReturned >= sizeof(int))
            {
                var state = (WTS_CONNECTSTATE_CLASS)Marshal.ReadInt32(buffer);
                return state.ToString();
            }

            return "Unknown";
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("wtsapi32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private enum WTS_INFO_CLASS
    {
        WTSInitialProgram = 0,
        WTSApplicationName = 1,
        WTSWorkingDirectory = 2,
        WTSOEMId = 3,
        WTSSessionId = 4,
        WTSUserName = 5,
        WTSWinStationName = 6,
        WTSDomainName = 7,
        WTSConnectState = 8
    }

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }
}
