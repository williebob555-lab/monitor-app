using MonitorApp.Peer.Node;
using MonitorApp.Peer.Node.Update;

if (args.Length >= 2 && args[0] == ApplyHelper.HelperArgument)
{
    // Root-only helper started by monitor-app-update.service; not part of the normal node.
    return await ApplyHelper.RunHelperAsync(
        args[1],
        NodeUpdateOptions.DefaultKeyFile(),
        "/var/lib/monitor-app-helper",
        Environment.GetEnvironmentVariable("MONITOR_APP_UPDATE_PACKAGE") is { Length: > 0 } kind && kind != "auto"
            ? kind
            : File.Exists("/etc/redhat-release") ? "rpm" : "deb",
        Environment.GetEnvironmentVariable("MONITOR_APP_UPDATE_CHANNEL") is { Length: > 0 } channel ? channel : "preview").ConfigureAwait(false);
}

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
