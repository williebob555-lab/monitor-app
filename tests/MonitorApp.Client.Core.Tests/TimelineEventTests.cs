using Xunit;
using MonitorApp.Client.Core.ViewModels.Shell;

namespace MonitorApp.Client.Core.Tests;

public class TimelineEventTests
{
    [Fact]
    public void EventsComeOnlyFromRealDataWindowedDedupedAndOrdered()
    {
        var now = DateTimeOffset.UtcNow;
        var from = now.AddHours(-24);
        AlertDto Alert(string id, string sev, string subject, double raisedAgo, double? resolvedAgo) =>
            new(id, 1, "rule", subject, "peer-a", sev, resolvedAgo is null ? "Active" : "Resolved", "T-" + id, "d",
                now.AddHours(-raisedAgo), resolvedAgo is null ? null : now.AddHours(-resolvedAgo.Value));
        AlertDto[] alerts =
        [
            Alert("crit", "Critical", "peer:peer-a", 5, 4),
            Alert("warn", "Warning", "disk:1", 3, null),
            Alert("info", "Info", "peer-a", 2, null),
            Alert("old", "Critical", "peer:peer-a", 48, null),
        ];
        var peers = new[]
        {
            new TrustedPeerDto("peer-a", "Alpha", "ab", "Active", now.AddHours(-30), null),
            new TrustedPeerDto("peer-b", null, "cd", "Revoked", now.AddHours(-10), now.AddHours(-1)),
        };
        var pairings = new[] { new PendingPairingDto("p1", "peer-c", "Gamma", "ef", "Pending", now.AddMinutes(-20), now.AddHours(1)) };

        // Duplicate alert rows (same alert listed twice) must not double up.
        var events = TimelineEventSource.Derive([.. alerts, alerts[0]], peers, pairings, from, now);

        var summary = string.Join(" | ", events.Select(e => $"{e.Kind}/{e.Severity}/{e.SubjectNodeId}"));
        Assert.True(events.Count == 7, "expected 7 events (out-of-window alert and 30h-old pairing dropped, duplicate removed) but got: " + summary);
        Assert.True(events.Select(e => e.Time).SequenceEqual(events.Select(e => e.Time).OrderBy(t => t)), "not time ordered: " + summary);
        var crit = events.Single(e => e.Kind == TimelineEventKind.Alert && e.Title == "T-crit");
        Assert.True(crit is { Severity: TimelineSeverity.Fault, SubjectNodeId: "peer-a" }, "peer: prefix must be stripped to a Scene node id: " + summary);
        Assert.Equal(TimelineSeverity.Warning, events.Single(e => e.Title == "T-warn").Severity);
        Assert.Equal("disk:1", events.Single(e => e.Title == "T-warn").SubjectNodeId);
        Assert.Contains(events, e => e.Kind == TimelineEventKind.AlertResolved && e.Title == "Resolved: T-crit");
        Assert.Contains(events, e => e.Kind == TimelineEventKind.Pairing && e.SubjectNodeId == "peer-c");
        Assert.DoesNotContain(events, e => e.Kind is TimelineEventKind.PeerOffline or TimelineEventKind.PeerStale or TimelineEventKind.PeerBack or TimelineEventKind.Update);
        Assert.Equal(summary, string.Join(" | ", TimelineEventSource.Derive(alerts.Reverse(), peers, pairings, from, now).Select(e => $"{e.Kind}/{e.Severity}/{e.SubjectNodeId}")));
    }
}
