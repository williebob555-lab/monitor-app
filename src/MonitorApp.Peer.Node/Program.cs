using MonitorApp.Peer.Node;

NodeOptions options;
try
{
    options = NodeOptions.Parse(args, NodeHost.IsServiceMode());
}
catch (ArgumentException exception)
{
    await Console.Error.WriteLineAsync($"monitor-app: invalid configuration: {exception.Message}").ConfigureAwait(false);
    return 2;
}

try
{
    using var host = NodeHost.Build(options);
    await host.RunAsync().ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    // Already logged critically by the hosted service; ensure a non-zero exit for SCM/systemd.
    await Console.Error.WriteLineAsync($"monitor-app: fatal: {exception.Message}").ConfigureAwait(false);
    return 1;
}

public partial class Program;
