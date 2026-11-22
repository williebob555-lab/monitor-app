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
            record.ResolvedAt is null ? null : record.ResolvedAt.ToDateTimeOffset());
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
    DateTimeOffset? AckedAt);

public sealed class AlertDeliveryUnauthorizedException(string message) : Exception(message);
