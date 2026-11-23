using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Collection.Host;

/// <summary>
/// Reads this OS install's boot menu and whether a one-time boot switch is possible from here (T059). Implementations
/// only read; they never change boot configuration. The result is published inside the host descriptor (T058).
/// </summary>
public interface IBootConfigurationProbe
{
    Task<BootConfiguration> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Used where no platform probe exists: reports switching as unsupported instead of guessing.</summary>
public sealed class UnknownBootConfigurationProbe : IBootConfigurationProbe
{
    public Task<BootConfiguration> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new BootConfiguration
        {
            BootLoader = "unknown",
            Support = BootSwitchSupport.Unsupported,
            UnsupportedReason = "Boot configuration detection is not available on this platform.",
        });
}
