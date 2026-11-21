namespace MonitorApp.Peer.Core;

public readonly record struct PeerProtocolVersion(uint Major, uint Minor);

public sealed record PeerProtocolRange(uint Major, uint MinimumMinor, uint MaximumMinor)
{
    public void Validate()
    {
        if (Major == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Major), "Protocol major must be nonzero.");
        }

        if (MinimumMinor > MaximumMinor)
        {
            throw new ArgumentException("Minimum minor cannot exceed maximum minor.");
        }
    }
}

public enum PeerCompatibilityStatus
{
    Compatible = 1,
    IncompatibleMajor = 2,
    NoOverlappingMinor = 3,
}

public sealed record PeerCompatibilityResult(
    PeerCompatibilityStatus Status,
    PeerProtocolVersion? SelectedVersion,
    string Reason)
{
    public bool IsCompatible => Status == PeerCompatibilityStatus.Compatible;
}

public static class PeerProtocolCompatibility
{
    public static PeerCompatibilityResult Negotiate(
        PeerProtocolRange local,
        PeerProtocolRange remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        local.Validate();
        remote.Validate();

        if (local.Major != remote.Major)
        {
            return new PeerCompatibilityResult(
                PeerCompatibilityStatus.IncompatibleMajor,
                null,
                "Peer protocol major versions are incompatible.");
        }

        var minimum = Math.Max(local.MinimumMinor, remote.MinimumMinor);
        var maximum = Math.Min(local.MaximumMinor, remote.MaximumMinor);
        if (minimum > maximum)
        {
            return new PeerCompatibilityResult(
                PeerCompatibilityStatus.NoOverlappingMinor,
                null,
                "Peer protocol minor ranges do not overlap.");
        }

        return new PeerCompatibilityResult(
            PeerCompatibilityStatus.Compatible,
            new PeerProtocolVersion(local.Major, maximum),
            string.Empty);
    }
}
