using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using RemoteDesktop.Service.Services;
using RemoteDesktop.Shared.Security;
using Serilog;
using System.Runtime.InteropServices;
using RemoteDesktop.Service.Utilities;

if (OperatingSystem.IsWindows())
{
    TryEnablePerMonitorDpiAwareness();
    PrivilegeHelper.EnableRequiredPrivileges();
}

var builder = Host.CreateApplicationBuilder(args);

if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "P2PRD";
    });
}

var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "P2PRD", "logs");
Directory.CreateDirectory(logDirectory);
AclHelper.HardenDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDirectory, "service-.log"),
        rollingInterval: RollingInterval.Day,
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        retainedFileCountLimit: 10,
        shared: true)
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Host.UseSerilog();

builder.Services.AddSingleton<HostConfigProvider>();
builder.Services.AddSingleton<LockoutManager>();
builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddSingleton<SignalingResolver>();
builder.Services.AddSingleton<WebSocketSignalingClient>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<MonitorService>();
builder.Services.AddSingleton<InputDesktopSwitcher>();
builder.Services.AddSingleton<CaptureService>();
builder.Services.AddSingleton<InputService>();
builder.Services.AddSingleton<WebRtcService>();
builder.Services.AddHostedService<SessionSwitchService>();
builder.Services.AddHostedService<ConfigPipeService>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<HostService>();

var host = builder.Build();
try
{
    await host.RunAsync();
}
finally
{
    Log.CloseAndFlush();
}

static void TryEnablePerMonitorDpiAwareness()
{
    try
    {
        _ = SetProcessDpiAwareness(ProcessDpiAwareness.PerMonitorAware);
    }
    catch
    {
        // Best effort; ignore failures on down-level hosts.
    }
}

internal enum ProcessDpiAwareness
{
    DpiUnaware = 0,
    SystemDpiAware = 1,
    PerMonitorAware = 2
}

[DllImport("Shcore.dll")]
internal static extern int SetProcessDpiAwareness(ProcessDpiAwareness awareness);
