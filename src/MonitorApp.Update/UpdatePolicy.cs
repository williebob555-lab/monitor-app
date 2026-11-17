namespace MonitorApp.Update;

public static class UpdatePolicy
{
    public static UpdateArtifact SelectArtifact(
        UpdateManifest manifest,
        ReleaseVersion currentVersion,
        string channel,
        string component,
        string platform,
        string architecture)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!string.Equals(manifest.Channel, channel, StringComparison.Ordinal))
        {
            throw new UpdateVerificationException(
                UpdateVerificationError.ChannelMismatch,
                "Update manifest channel does not match the configured channel.");
        }

        if (manifest.Version <= currentVersion)
        {
            throw new UpdateVerificationException(
                UpdateVerificationError.DowngradeOrReinstall,
                "Update version must be newer than the installed version.");
        }

        if (currentVersion < manifest.MinimumCompatibleVersion)
        {
            throw new UpdateVerificationException(
                UpdateVerificationError.ManualUpgradeRequired,
                "Installed version is too old for this automated update.");
        }

        return manifest.Artifacts.SingleOrDefault(artifact =>
                   string.Equals(artifact.Component, component, StringComparison.Ordinal) &&
                   string.Equals(artifact.Platform, platform, StringComparison.Ordinal) &&
                   string.Equals(artifact.Architecture, architecture, StringComparison.Ordinal))
               ?? throw new UpdateVerificationException(
                   UpdateVerificationError.NoMatchingArtifact,
                   "Update manifest does not contain an artifact for this target.");
    }
}
