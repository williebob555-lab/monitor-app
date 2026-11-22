using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using MonitorApp.Peer.LocalApi;
using MonitorApp.Peer.Node.Update;
using MonitorApp.Update;

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
        AddUpdateAgent(builder.Services, options);
        return builder.Build();
    }

    /// <summary>
    /// Registers the update agent after the node service so the health marker is written only once the node started.
    /// Update configuration problems never prevent the node from starting: the agent reports itself disabled.
    /// </summary>
    private static void AddUpdateAgent(IServiceCollection services, NodeOptions options)
    {
        NodeUpdateOptions update;
        try
        {
            update = NodeUpdateOptions.FromEnvironment(options);
        }
        catch (ArgumentException)
        {
            update = NodeUpdateOptions.FromEnvironment(options, _ => null);
        }

        var trust = NodeUpdateTrust.TryLoad(update.KeyFilePath);
        services.AddSingleton(update);
        services.AddSingleton(provider =>
        {
            TrustedUpdateKey? key = null;
            IManifestFetcher? fetcher = null;
            IArtifactDownloader? downloader = null;
            if (trust is not null && update.Repository.Length > 0)
            {
                var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MonitorApp-Node-Updater");
                key = new TrustedUpdateKey(trust.KeyId, trust.PublicKeySpki);
                fetcher = new GitHubPreviewManifestFetcher(http, update.ApiBase, update.Repository);
                downloader = new HttpArtifactDownloader(http);
            }

            return new NodeUpdateAgent(
                update,
                key,
                fetcher,
                downloader,
                new ApplyHelper.HandoffApplier(update),
                provider.GetRequiredService<ILogger<NodeUpdateAgent>>());
        });
        services.AddSingleton<ILocalUpdateController>(provider => provider.GetRequiredService<NodeUpdateAgent>());
        services.AddHostedService(provider => provider.GetRequiredService<NodeUpdateAgent>());
    }
}
