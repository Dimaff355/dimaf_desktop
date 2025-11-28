namespace RemoteDesktop.Shared.Models;

public sealed record MonitorDescriptor
(
    string Id,
    string Name,
    int Width,
    int Height,
    double DpiScale
);
