using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesktop.Service.Services;
using RemoteDesktop.Shared.Security;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<HostConfigProvider>();
builder.Services.AddSingleton<LockoutManager>();
builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddSingleton<SignalingResolver>();
builder.Services.AddSingleton<WebSocketSignalingClient>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<HostService>();

var host = builder.Build();
await host.RunAsync();
