using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MonitorApp.Peer.Core;

namespace MonitorApp.Peer.Service;

public static class PeerHostApplication
{
    public static Task<WebApplication> CreateAsync(
        string[] args,
        Action<IServiceCollection>? configureServices = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var builder = WebApplication.CreateBuilder(args);
        var options = PeerHostOptions.FromConfiguration(builder.Configuration);

        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(
                options.BindAddress,
                options.Port,
                listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<PeerRuntimeState>();
        builder.Services.AddGrpc(grpc =>
        {
            grpc.EnableDetailedErrors = false;
            grpc.MaxReceiveMessageSize = options.MaximumMessageBytes;
            grpc.MaxSendMessageSize = options.MaximumMessageBytes;
        });
        configureServices?.Invoke(builder.Services);
        builder.Services.TryAddSingleton<IPeerSessionCoordinator, UnconfiguredPeerSessionCoordinator>();
        builder.Services.TryAddSingleton<IPeerPairingCoordinator, UnconfiguredPeerPairingCoordinator>();
        builder.Services.TryAddSingleton<PairingShortCodeRegistry>();

        var app = builder.Build();
        var runtime = app.Services.GetRequiredService<PeerRuntimeState>();
        app.Lifetime.ApplicationStarted.Register(runtime.Started);
        app.Lifetime.ApplicationStopping.Register(runtime.Stopping);

        app.MapGet("/health", (PeerRuntimeState state) => Results.Ok(new
        {
            status = state.IsRunning ? "healthy" : "starting",
            protocolMajor = Contracts.PeerContractRules.ProtocolMajor,
        }));
        app.MapGet("/diagnostics/local", (
            HttpContext context,
            PeerRuntimeState state,
            PeerHostOptions hostOptions) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !System.Net.IPAddress.IsLoopback(remote))
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                status = state.IsRunning ? "running" : "stopping",
                bindScope = System.Net.IPAddress.IsLoopback(hostOptions.BindAddress)
                    ? "loopback"
                    : "private",
                hostOptions.Port,
                hostOptions.MaximumMessageBytes,
                hostOptions.MaximumMessagesPerSession,
            });
        });
        app.MapGrpcService<PeerPairingGrpcService>();
        app.MapGrpcService<PeerPairingCodeGrpcService>();
        app.MapGrpcService<PeerSessionGrpcService>();
        return Task.FromResult(app);
    }
}
