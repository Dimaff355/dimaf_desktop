using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Service.Services;
using RemoteDesktop.Shared.Security;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "P2PRD", "logs");
Directory.CreateDirectory(logDirectory);

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
builder.Services.AddSingleton<CaptureService>();
builder.Services.AddSingleton<InputService>();
builder.Services.AddSingleton<WebRtcService>();
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
