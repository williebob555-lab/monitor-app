using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonitorApp.Peer.Collection;

namespace MonitorApp.Peer.Node;

/// <summary>Owns the <see cref="PeerNodeRuntime"/> lifetime inside the Generic Host.</summary>
public sealed partial class PeerNodeHostedService(
    NodeOptions options,
    ILogger<PeerNodeHostedService> logger,
    MonitorApp.Peer.LocalApi.ILocalUpdateController? updates = null) : IHostedService
{
    private PeerNodeRuntime? runtime;

    public Uri? AdminAddress => runtime?.AdminAddress;

    public string? PeerId => runtime?.Identity.PeerId;

    public Task<PeerCollectionRunResult> CollectOnceAsync(CancellationToken cancellationToken = default) =>
        (runtime ?? throw new InvalidOperationException("Node is not running.")).CollectOnceAsync(cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        PeerNodeRuntime started;
        try
        {
            started = await PeerNodeRuntime.StartAsync(options, null, updates, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Identity, permission, data-directory and unsupported-platform failures end up here;
            // the host then exits non-zero so systemd / the Windows SCM records a failed start.
            LogStartFailed(exception, options.DataDirectory, exception.Message);
            throw;
        }

        runtime = started;
        var fingerprint = Convert.ToHexString(started.Identity.PublicKeyFingerprint).ToLowerInvariant();
        LogStarted(started.Identity.PeerId, fingerprint);
        LogListening(started.AdminAddress, options.DataDirectory, options.PeerBindAddress, options.PeerPort);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var current = Interlocked.Exchange(ref runtime, null);
        if (current is null)
        {
            return;
        }

        LogStopping();
        await current.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Peer node failed to start (data directory {DataDirectory}): {Reason}")]
    private partial void LogStartFailed(Exception exception, string dataDirectory, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Monitor App peer {PeerId} started; fingerprint {Fingerprint}")]
    private partial void LogStarted(string peerId, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Local administration (loopback only): {AdminAddress}; data directory {DataDirectory}; peer listener {Bind}:{Port}")]
    private partial void LogListening(Uri? adminAddress, string dataDirectory, System.Net.IPAddress bind, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping Monitor App peer node")]
    private partial void LogStopping();
}
