using System.Globalization;

namespace MonitorApp.Client.Core.ViewModels.Shell;

public enum TimelineEventKind
{
    Alert,
    AlertResolved,
    PeerOffline,
    PeerStale,
    PeerBack,
    Pairing,
    Update,
    Other,
}

/// <summary>How a marker is drawn: red fault, amber warning, gray offline, cyan info.</summary>
public enum TimelineSeverity
{
    Info,
    Warning,
    Fault,
    Offline,
}

/// <summary>
/// One thing that happened, independent of any layout. <see cref="SubjectNodeId"/> is a Scene node id (bare peer id, or the
/// device subject id) so any interface can select the node the event is about.
/// </summary>
public sealed record TimelineEvent(
    DateTimeOffset Time,
    TimelineEventKind Kind,
    TimelineSeverity Severity,
    string? SubjectNodeId,
    string Title,
    string Detail);

/// <summary>An event placed on the timeline: <see cref="Fraction"/> is 0 (window start) to 1 (now).</summary>
public sealed record TimelineMarker(TimelineEvent Event, double Fraction)
{
    public string Tooltip =>
        $"{Event.Title} · {Event.Time.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)}"
        + (string.IsNullOrWhiteSpace(Event.Detail) ? string.Empty : "\n" + Event.Detail);

    public bool IsFault => Event.Severity == TimelineSeverity.Fault;

    public bool IsWarning => Event.Severity == TimelineSeverity.Warning;

    public bool IsOffline => Event.Severity == TimelineSeverity.Offline;

    public bool IsInfo => Event.Severity == TimelineSeverity.Info;

    public string AutomationName => $"{Event.Severity} event: {Event.Title}";
}

/// <summary>
/// Derives events only from data the source really exposes: alerts (raised and resolved, with timestamps) and the trusted
/// peers / pending pairings lists. The API has no peer online/offline transition history, so PeerOffline/PeerStale/PeerBack
/// and Update are never derived here (nothing is fabricated); they exist so a source that gains that data can emit them.
/// </summary>
public static class TimelineEventSource
{
    public static string? NodeIdFor(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        return subject.StartsWith(Scene.SceneProjector.PeerSubjectPrefix, StringComparison.Ordinal)
            ? subject[Scene.SceneProjector.PeerSubjectPrefix.Length..]
            : subject;
    }

    public static async Task<IReadOnlyList<TimelineEvent>> LoadAsync(
        IPeerDataSource source, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        IReadOnlyList<AlertDto> alerts = [];
        if (source is IAlertSource alertSource)
        {
            try
            {
                alerts = await alertSource.GetAlertsAsync(500, ct);
            }
            catch (LocalApiException)
            {
                // Markers are decoration; a failed alert read must not break the timeline.
            }
        }

        IReadOnlyList<TrustedPeerDto> peers = [];
        IReadOnlyList<PendingPairingDto> pairings = [];
        try
        {
            peers = await source.GetPeersAsync(ct);
            pairings = await source.GetPairingsAsync(ct);
        }
        catch (LocalApiException)
        {
        }

        return Derive(alerts, peers, pairings, from, to);
    }

    /// <summary>Pure derivation: windowed to [from, to], de-duplicated, ordered by time then kind then title.</summary>
    public static IReadOnlyList<TimelineEvent> Derive(
        IEnumerable<AlertDto> alerts, IEnumerable<TrustedPeerDto> peers, IEnumerable<PendingPairingDto> pairings,
        DateTimeOffset from, DateTimeOffset to)
    {
        var events = new List<TimelineEvent>();
        foreach (var alert in alerts)
        {
            var severity = alert.Severity.ToLowerInvariant() switch
            {
                "critical" => TimelineSeverity.Fault,
                "warning" => TimelineSeverity.Warning,
                _ => TimelineSeverity.Info,
            };
            var subject = NodeIdFor(alert.SubjectId) ?? NodeIdFor(alert.OriginPeerId);
            events.Add(new(alert.RaisedAt, TimelineEventKind.Alert, severity, subject, alert.Title, alert.Detail));
            if (alert.ResolvedAt is { } resolved)
            {
                events.Add(new(resolved, TimelineEventKind.AlertResolved, TimelineSeverity.Info, subject, "Resolved: " + alert.Title, alert.Detail));
            }
        }

        foreach (var peer in peers)
        {
            var name = peer.DisplayName ?? peer.PeerId;
            events.Add(new(peer.PairedAt, TimelineEventKind.Pairing, TimelineSeverity.Info, peer.PeerId, "Paired " + name, "Device trusted"));
            if (peer.RevokedAt is { } revoked)
            {
                events.Add(new(revoked, TimelineEventKind.Other, TimelineSeverity.Info, peer.PeerId, "Revoked " + name, "Trust withdrawn"));
            }
        }

        foreach (var pairing in pairings)
        {
            var name = pairing.DisplayName ?? pairing.RemotePeerId;
            events.Add(new(pairing.CreatedAt, TimelineEventKind.Pairing, TimelineSeverity.Info, pairing.RemotePeerId, "Pairing request from " + name, pairing.ApprovalState));
        }

        return events
            .Where(e => e.Time >= from && e.Time <= to)
            .DistinctBy(e => (e.Time, e.Kind, e.SubjectNodeId, e.Title))
            .OrderBy(e => e.Time)
            .ThenBy(e => e.Kind)
            .ThenBy(e => e.Title, StringComparer.Ordinal)
            .ToList();
    }
}
