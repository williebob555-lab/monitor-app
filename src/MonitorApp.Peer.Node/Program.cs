using MonitorApp.Peer.Node;

var options = NodeOptions.Parse(args);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
await using var runtime = await PeerNodeRuntime.StartAsync(options, shutdown.Token).ConfigureAwait(false);
Console.WriteLine($"Monitor App peer: {runtime.Identity.PeerId}");
Console.WriteLine($"Fingerprint: {Convert.ToHexString(runtime.Identity.PublicKeyFingerprint).ToLowerInvariant()}");
Console.WriteLine($"Local administration: {runtime.AdminAddress}");
Console.WriteLine("Implemented preview: local identity/trust/observations and authenticated peer sessions. Initial peer invitation UI is still in development.");
try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    // Normal Ctrl+C shutdown.
}

public partial class Program;
