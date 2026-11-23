using MonitorApp.Peer.Node;
using MonitorApp.Peer.Node.Update;

if (args.Length >= 2 && args[0] == ApplyHelper.HelperArgument)
{
    // Privileged helper; not part of the normal node. Linux: root, started by monitor-app-update.path.
    // Windows: SYSTEM, started on demand by the MonitorAppUpdate scheduled task with an explicit --install-root.
    if (OperatingSystem.IsWindows())
    {
        return args.Length >= 4 && args[2] == WindowsApplyHelper.InstallRootArgument
            ? await WindowsApplyHelper.RunWithHousekeepingAsync(
                args[1],
                args[3],
                NodeUpdateOptions.DefaultKeyFile(),
                Environment.GetEnvironmentVariable("MONITOR_APP_UPDATE_CHANNEL") is { Length: > 0 } windowsChannel ? windowsChannel : "preview").ConfigureAwait(false)
            : 2;
    }

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
