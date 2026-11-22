using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Replication;
using Xunit;

namespace MonitorApp.Peer.Alerts.Tests;

public sealed class AlertRuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static AlertRulesConfig Config(TimeSpan? unavailable = null) => AlertRulesConfig.Default with
    {
        UnavailableAfter = unavailable ?? TimeSpan.FromMinutes(2),
    };

    [Fact]
    public async Task DiskBelowThresholdRaisesOnceWithStableIdThenResolvesAndStartsNewEpisode()
    {
        await using var peer = await TestPeer.CreateAsync(T0);
        var evaluator = peer.Evaluator(Config());
        await peer.CollectAsync(T0, freeBytes: 5);

        var first = await evaluator.EvaluateOnceAsync();
        var second = await evaluator.EvaluateOnceAsync();
        var restarted = await peer.Evaluator(Config()).EvaluateOnceAsync();

        Assert.Equal(1, first.Raised);
        Assert.Equal(0, second.Raised);
        Assert.Equal(0, restarted.Raised);
        var active = Assert.Single(await peer.Alerts.ReadActiveAsync());
        Assert.Equal(Alert.CreateId(peer.PublicIdentity.PeerId, "threshold.disk_free", peer.Subject, T0), active.AlertId);
        Assert.Equal(T0, active.RaisedAt);
        Assert.Equal(AlertSeverity.Critical, active.Severity);

        await peer.CollectAsync(T0.AddMinutes(1), freeBytes: 50);
        var resolved = await evaluator.EvaluateOnceAsync();
        Assert.Equal(1, resolved.Resolved);
        Assert.Empty(await peer.Alerts.ReadActiveAsync());
        var stored = await peer.Alerts.ReadAsync(active.AlertId);
        Assert.Equal(AlertState.Resolved, stored!.State);
        Assert.Equal(2u, stored.Revision);
        Assert.Equal(T0.AddMinutes(1), stored.ResolvedAt);

        await peer.CollectAsync(T0.AddMinutes(2), freeBytes: 4);
        Assert.Equal(1, (await evaluator.EvaluateOnceAsync()).Raised);
        var next = Assert.Single(await peer.Alerts.ReadActiveAsync());
        Assert.NotEqual(active.AlertId, next.AlertId);
    }

    [Fact]
    public async Task SustainedCpuRaisesOnlyAfterTheWindowAndStartsAtTheRunStart()
    {
        await using var peer = await TestPeer.CreateAsync(T0);
        var evaluator = peer.Evaluator(Config());
        for (var minute = 0; minute <= 9; minute++)
        {
            await peer.CollectAsync(T0.AddMinutes(minute), cpu: 0.95);
            Assert.Equal(0, (await evaluator.EvaluateOnceAsync()).Raised);
        }

        await peer.CollectAsync(T0.AddMinutes(10), cpu: 0.97);
        Assert.Equal(1, (await evaluator.EvaluateOnceAsync()).Raised);
        var alert = Assert.Single(await peer.Alerts.ReadActiveAsync());
        Assert.Equal("threshold.cpu_high", alert.RuleId);
        Assert.Equal(T0, alert.RaisedAt);

        await peer.CollectAsync(T0.AddMinutes(11), cpu: 0.2);
        Assert.Equal(1, (await evaluator.EvaluateOnceAsync()).Resolved);
    }

    [Fact]
    public async Task ABriefCpuDipRestartsTheSustainedWindow()
    {
        await using var peer = await TestPeer.CreateAsync(T0);
        var evaluator = peer.Evaluator(Config());
        for (var minute = 0; minute <= 12; minute++)
        {
            var cpu = minute == 6 ? 0.1 : 0.95;
            await peer.CollectAsync(T0.AddMinutes(minute), cpu: cpu);
            await evaluator.EvaluateOnceAsync();
        }

        // Only 6 minutes have been continuously high since the dip, so nothing may be active yet.
        Assert.Empty(await peer.Alerts.ReadActiveAsync());
        await peer.CollectAsync(T0.AddMinutes(17), cpu: 0.95);
        Assert.Equal(1, (await evaluator.EvaluateOnceAsync()).Raised);
        Assert.Equal(T0.AddMinutes(7), Assert.Single(await peer.Alerts.ReadActiveAsync()).RaisedAt);
    }

    [Fact]
    public async Task UnavailableObservationRaisesAfterGraceAndResolvesWhenValidAgain()
    {
        await using var peer = await TestPeer.CreateAsync(T0);
        var evaluator = peer.Evaluator(Config(TimeSpan.FromMinutes(2)));
        await peer.CollectAsync(T0, cpu: 0.1, quality: ObservationQuality.Unavailable);
        Assert.Equal(0, (await evaluator.EvaluateOnceAsync()).Raised);
        await peer.CollectAsync(T0.AddMinutes(1), cpu: 0.1, quality: ObservationQuality.Unavailable);
        Assert.Equal(0, (await evaluator.EvaluateOnceAsync()).Raised);
        await peer.CollectAsync(T0.AddMinutes(2), cpu: 0.1, quality: ObservationQuality.Error);
        Assert.Equal(1, (await evaluator.EvaluateOnceAsync()).Raised);
        var alert = Assert.Single(await peer.Alerts.ReadActiveAsync());
        Assert.Equal(AlertRulesConfig.ObservationUnavailableRuleId, alert.RuleId);
        Assert.Equal($"{peer.Subject}#system.cpu.utilization", alert.SubjectId);
        Assert.Equal(T0, alert.RaisedAt);

        await peer.CollectAsync(T0.AddMinutes(3), cpu: 0.1);
        Assert.Equal(1, (await evaluator.EvaluateOnceAsync()).Resolved);
    }

    [Fact]
    public async Task PeerNotSeenRaisesAtTheDeadlineAndResolvesOnRevocation()
    {
        await using var desktop = await TestPeer.CreateAsync(T0);
        await using var phone = await TestPeer.CreateAsync(T0);
        await desktop.TrustAsync(phone.PublicIdentity, T0, "Pixel");
        var evaluator = desktop.Evaluator(Config());

        desktop.SetNow(T0.AddMinutes(9));
        Assert.Equal(0, (await evaluator.EvaluateOnceAsync()).Raised);
        desktop.SetNow(T0.AddMinutes(25));
        Assert.Equal(1, (await evaluator.EvaluateOnceAsync()).Raised);
        var alert = Assert.Single(await desktop.Alerts.ReadActiveAsync());
        Assert.Equal(AlertRulesConfig.PeerSilentRuleId, alert.RuleId);
        Assert.Equal(phone.PublicIdentity.PeerId, alert.SubjectId);
        Assert.Equal(T0.AddMinutes(10), alert.RaisedAt);
        Assert.Equal(0, (await evaluator.EvaluateOnceAsync()).Raised);

        await desktop.Store.RevokeTrustedPeerAsync(phone.PublicIdentity.PeerId, T0.AddMinutes(26));
        Assert.Equal(1, (await evaluator.EvaluateOnceAsync()).Resolved);
    }

    [Fact]
    public async Task PeerSeenThroughASessionTouchDefersTheSilentAlert()
    {
        await using var desktop = await TestPeer.CreateAsync(T0);
        await using var phone = await TestPeer.CreateAsync(T0);
        await desktop.TrustAsync(phone.PublicIdentity, T0);
        await desktop.Alerts.TouchPeerAsync(phone.PublicIdentity.PeerId, T0.AddMinutes(20));
        desktop.SetNow(T0.AddMinutes(25));
        Assert.Equal(0, (await desktop.Evaluator(Config()).EvaluateOnceAsync()).Raised);
        desktop.SetNow(T0.AddMinutes(31));
        Assert.Equal(1, (await desktop.Evaluator(Config()).EvaluateOnceAsync()).Raised);
    }

    [Fact]
    public async Task EvaluationIsDeterministicAcrossIndependentPeersWithIdenticalInputs()
    {
        await using var a = await TestPeer.CreateAsync(T0);
        await using var b = await TestPeer.CreateAsync(T0);
        foreach (var peer in new[] { a, b })
        {
            await peer.CollectAsync(T0, freeBytes: 3, cpu: 0.99);
            for (var minute = 1; minute <= 10; minute++)
            {
                await peer.CollectAsync(T0.AddMinutes(minute), freeBytes: 3, cpu: 0.99);
            }

            await peer.Evaluator(Config()).EvaluateOnceAsync();
        }

        var fromA = (await a.Alerts.ReadActiveAsync()).Select(x => (x.RuleId, x.RaisedAt, x.Title, x.Detail.Replace(a.Subject, "S"))).ToArray();
        var fromB = (await b.Alerts.ReadActiveAsync()).Select(x => (x.RuleId, x.RaisedAt, x.Title, x.Detail.Replace(b.Subject, "S"))).ToArray();
        Assert.Equal(2, fromA.Length);
        Assert.Equal(fromA, fromB);
    }

    [Fact]
    public async Task ReplicatedObservationsOnANonOwnerPeerNeverRaiseAlerts()
    {
        await using var owner = await TestPeer.CreateAsync(T0);
        await using var replica = await TestPeer.CreateAsync(T0);
        await replica.TrustAsync(owner.PublicIdentity, T0);
        await owner.CollectAsync(T0, freeBytes: 2);
        var identity = (await owner.Store.ReadLocalIdentityAsync())!;
        var batch = await new OriginReplicationCoordinator(owner.Store).ReadAsync(new OriginRangeRequest
        {
            RequestId = "r1",
            OriginPeerId = identity.PeerId,
            OriginStreamId = identity.OriginStreamId,
            FirstSequence = 1,
            LastSequence = identity.NextSequence - 1,
            MaximumRecordCount = 100,
            MaximumBatchBytes = 1024 * 1024,
        });
        await new OriginReplicationCoordinator(replica.Store).ApplyAsync(owner.PublicIdentity.PeerId, batch);
        Assert.NotEmpty(await replica.Store.ReadCurrentObservationsAsync());

        var ownerResult = await owner.Evaluator(Config()).EvaluateOnceAsync();
        var replicaResult = await replica.Evaluator(Config()).EvaluateOnceAsync();

        Assert.Equal(1, ownerResult.Raised);
        Assert.Equal(0, replicaResult.Raised);
        Assert.Empty(await replica.Alerts.ReadActiveAsync());
    }

    [Fact]
    public void InvalidConfigurationIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (AlertRulesConfig.Default with { PeerSilentAfter = TimeSpan.Zero }).Validate());
        var duplicate = AlertRulesConfig.Default with { Thresholds = [.. AlertRulesConfig.Default.Thresholds, AlertRulesConfig.Default.Thresholds[0]] };
        Assert.Throws<ArgumentException>(duplicate.Validate);
    }
}
