namespace MonitorApp.Update;

/// <summary>
/// Explicit, audited permission to install one specific older signed version. It never relaxes
/// signature, channel, target, size, or hash checks.
/// </summary>
public sealed record DowngradeAuthorization
{
    public DowngradeAuthorization(ReleaseVersion targetVersion, string approvedBy, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        TargetVersion = targetVersion;
        ApprovedBy = approvedBy;
        Reason = reason;
    }

    public ReleaseVersion TargetVersion { get; }

    public string ApprovedBy { get; }

    public string Reason { get; }
}

/// <summary>Explicit user/operator consent to apply a staged update. There is no implicit approval.</summary>
public sealed record UpdateApproval(string ApprovedBy, DateTimeOffset ApprovedAtUtc);
