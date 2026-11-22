using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Peer.Alerts;

/// <summary>
/// One alert episode at its latest revision. Revision 1 is the raise, revision 2 the resolve.
/// <see cref="OriginPeerId"/> is the single execution owner that evaluated the rule.
/// </summary>
public sealed record Alert(
    string AlertId,
    uint Revision,
    string RuleId,
    string SubjectId,
    string OriginPeerId,
    AlertSeverity Severity,
    AlertState State,
    string Title,
    string Detail,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ResolvedAt)
{
    /// <summary>True for the one-time escalation delivery of a raised, unacknowledged Critical alert (T051); never stored on the owner's alert row.</summary>
    public bool Escalated { get; init; }

    /// <summary>
    /// Stable id: the same owner, rule, subject and episode start always yield the same id, so re-evaluation
    /// and crash recovery can never create a second alert for one episode.
    /// </summary>
    public static string CreateId(string originPeerId, string ruleId, string subjectId, DateTimeOffset episodeStart)
    {
        var text = string.Join(
            '\n', originPeerId, ruleId, subjectId, episodeStart.UtcTicks.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).ToLowerInvariant();
    }

    public AlertRecord ToContract()
    {
        var record = new AlertRecord
        {
            SchemaVersion = PeerContractRules.AlertSchemaVersion,
            AlertId = AlertId,
            Revision = Revision,
            RuleId = RuleId,
            SubjectId = SubjectId,
            OriginPeerId = OriginPeerId,
            Severity = Severity,
            State = State,
            Title = Title,
            Detail = Detail,
            RaisedAt = Timestamp.FromDateTimeOffset(RaisedAt),
            Escalated = Escalated,
        };
        if (ResolvedAt is { } resolved)
        {
            record.ResolvedAt = Timestamp.FromDateTimeOffset(resolved);
        }

        return record;
    }

    public static Alert FromContract(AlertRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new Alert(
            record.AlertId,
            record.Revision,
            record.RuleId,
            record.SubjectId,
            record.OriginPeerId,
            record.Severity,
            record.State,
            record.Title,
            record.Detail,
            record.RaisedAt.ToDateTimeOffset(),
            record.ResolvedAt is null ? null : record.ResolvedAt.ToDateTimeOffset())
        {
            Escalated = record.Escalated,
        };
    }
}

/// <summary>An alert stored on a recipient (phone) with its local notification bookkeeping.</summary>
public sealed record InboxAlert(Alert Alert, DateTimeOffset ReceivedAt, uint NotifiedRevision)
{
    public bool NeedsNotification => NotifiedRevision < Alert.Revision;
}

public sealed record OutboxEntry(
    string RecipientPeerId,
    string AlertId,
    uint Revision,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset ExpiresAt,
    int Attempts,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? AckedAt)
{
    /// <summary>True for the one-time escalation delivery of the alert (T051); false for the raise or resolve.</summary>
    public bool Escalation { get; init; }
}

public sealed class AlertDeliveryUnauthorizedException(string message) : Exception(message);

/// <summary>
/// One recipient's delivery preferences. <see cref="MinimumSeverity"/> Unspecified means every severity;
/// <see cref="MutedRuleKinds"/> are rule ids the recipient never wants.
/// </summary>
public sealed record AlertPreferences(
    string RecipientPeerId,
    AlertSeverity MinimumSeverity,
    IReadOnlyList<string> MutedRuleKinds,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Wire/API name of a minimum severity: "All" (Unspecified), "Info", "Warning" or "Critical".</summary>
    public static string SeverityName(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Info => "Info",
        AlertSeverity.Warning => "Warning",
        AlertSeverity.Critical => "Critical",
        _ => "All",
    };

    /// <summary>Parses <see cref="SeverityName"/> output case-insensitively; null, empty and "All" mean every severity.</summary>
    public static bool TryParseSeverity(string? text, out AlertSeverity severity)
    {
        severity = AlertSeverity.Unspecified;
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var candidate in new[] { AlertSeverity.Info, AlertSeverity.Warning, AlertSeverity.Critical })
        {
            if (value.Equals(SeverityName(candidate), StringComparison.OrdinalIgnoreCase))
            {
                severity = candidate;
                return true;
            }
        }

        return false;
    }
}
