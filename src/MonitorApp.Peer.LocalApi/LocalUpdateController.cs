namespace MonitorApp.Peer.LocalApi;

/// <summary>Read-only view of the node's update state for the local API and desktop client.</summary>
public sealed record LocalUpdateStatus(
    string Policy,
    string InstalledVersion,
    string Stage,
    string? TargetVersion,
    string? Detail,
    DateTimeOffset? LastCheckedUtc,
    string? LastError,
    bool ApprovalPending,
    string? TargetDigest = null);

/// <summary>One install outcome from the update audit log (Applied, RolledBack or Failed).</summary>
public sealed record LocalUpdateEvent(string Version, string Outcome, DateTimeOffset At, string? Detail);

/// <summary>Optional companion of <see cref="ILocalUpdateController"/>: the install outcomes recorded in the audit log.</summary>
public interface ILocalUpdateHistory
{
    IReadOnlyList<LocalUpdateEvent> GetUpdateHistory(DateTimeOffset fromUtc, DateTimeOffset toUtc);
}

/// <summary>Implemented by the node's update agent; keeps this project independent of the update library.</summary>
public interface ILocalUpdateController
{
    LocalUpdateStatus GetStatus();

    /// <summary>Approve the staged update. Returns false when nothing is staged and awaiting approval.</summary>
    Task<bool> ApproveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Check for a new release now (the person pressed "Check for updates") instead of waiting for the next periodic check.
    /// Never installs anything: a found update is downloaded, verified and staged for approval as usual.
    /// </summary>
    Task CheckNowAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Approve only when the staged update is exactly <paramref name="version"/> with artifact SHA-256 <paramref name="digestHex"/>
    /// (hex, case-insensitive). Returns false when nothing matching is staged and awaiting approval.
    /// </summary>
    async Task<bool> ApproveAsync(string version, string digestHex, CancellationToken cancellationToken)
    {
        var status = GetStatus();
        return status.ApprovalPending &&
            string.Equals(status.TargetVersion, version, StringComparison.Ordinal) &&
            string.Equals(status.TargetDigest, digestHex, StringComparison.OrdinalIgnoreCase) &&
            await ApproveAsync(cancellationToken).ConfigureAwait(false);
    }
}
