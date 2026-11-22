using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace MonitorApp.Peer.Node;

public static class NodeHost
{
    public const string WindowsServiceName = "MonitorApp";

    public static bool IsServiceMode() => WindowsServiceHelpers.IsWindowsService() || SystemdHelpers.IsSystemdService();

    /// <summary>
    /// Builds the Generic Host. Works as a console app, a Windows service (SCM lifetime) and a systemd
    /// Type=notify service (sd_notify READY/STOPPING, journald priority prefixes).
    /// </summary>
    public static IHost Build(NodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddSimpleConsole(console => console.SingleLine = true);
        builder.Services.AddWindowsService(service => service.ServiceName = WindowsServiceName);
        builder.Services.AddSystemd();
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(20));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<PeerNodeHostedService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<PeerNodeHostedService>());
        return builder.Build();
    }
}
