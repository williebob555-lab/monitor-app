using System.Text;
using Microsoft.Data.Sqlite;
using MonitorApp.Peer.Alerts;
using Xunit;

namespace MonitorApp.Peer.Alerts.Tests;

/// <summary>T054: per-peer alert rule overrides change when alerts fire, are validated server-side, and persist across a schema upgrade.</summary>
public sealed class AlertRuleOverrideTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every threshold rule fires on the first breaching sample so the test is about the threshold, not the hold time.</summary>
    private static AlertRulesConfig Config() => AlertRulesConfig.Default with
    {
        RefireCooldown = TimeSpan.Zero,
        EscalationAfter = TimeSpan.Zero,
        Thresholds = [.. AlertRulesConfig.Default.Thresholds.Select(rule => rule with { Sustained = TimeSpan.Zero })],
    };

    private static async Task<string> DumpAsync(TestPeer peer)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Active alerts:");
        foreach (var alert in await peer.Alerts.ReadActiveAsync())
        {
            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  {alert.RuleId} subject={alert.SubjectId} raised={alert.RaisedAt:O} detail={alert.Detail}");
        }

        builder.AppendLine("Stored overrides:");
        foreach (var item in await peer.Alerts.ReadRuleOverridesAsync())
        {
            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  {item.PeerId} {item.RuleId} enabled={item.Enabled} threshold={item.Threshold}");
        }

        return builder.ToString();
    }

    private static async Task<string[]> ActiveRulesAsync(TestPeer peer) =>
        [.. (await peer.Alerts.ReadActiveAsync()).Select(alert => alert.RuleId).Order(StringComparer.Ordinal)];

    private static async Task EvaluateExpectingAsync(TestPeer peer, AlertEvaluator evaluator, string step, string[] expectedActiveRules)
    {
        var result = await evaluator.EvaluateOnceAsync();
        var active = await ActiveRulesAsync(peer);
        Assert.True(expectedActiveRules.Order(StringComparer.Ordinal).SequenceEqual(active),
            $"{step}: expected active rules [{string.Join(", ", expectedActiveRules)}] but found [{string.Join(", ", active)}] " +
            $"(raised {result.Raised}, resolved {result.Resolved}).\n{await DumpAsync(peer)}");
    }

    [Fact]
    public async Task OverridesMoveWhenEachRuleFiresDisableResolvesAndResetRestoresTheDefaults()
    {
        await using var desktop = await TestPeer.CreateAsync(T0);
        await using var phone = await TestPeer.CreateAsync(T0);
        await desktop.TrustAsync(phone.PublicIdentity, T0, "Pixel");
        var local = desktop.PublicIdentity.PeerId;
        var watched = phone.PublicIdentity.PeerId;
        var evaluator = desktop.Evaluator(Config());

        // CPU 80%, memory 85%, disk 20% free: all healthy at the built-in thresholds (90, 90, 10). The phone has been quiet 6 minutes (limit 10).
        await desktop.CollectAsync(T0, cpu: 0.80, freeBytes: 20, memoryUsed: 85);
        desktop.SetNow(T0.AddMinutes(6));
        await EvaluateExpectingAsync(desktop, evaluator, "defaults", []);

        // Tighter user thresholds for this peer: CPU above 70, memory above 80, disk free below 30, peer not seen after 5 minutes.
        await desktop.Alerts.SetRuleOverridesAsync(local, [
            new(AlertRuleCatalog.CpuRuleId, true, 70),
            new(AlertRuleCatalog.MemoryRuleId, true, 80),
            new(AlertRuleCatalog.DiskRuleId, true, 30)], T0.AddMinutes(6));
        await desktop.Alerts.SetRuleOverridesAsync(watched, [new(AlertRulesConfig.PeerSilentRuleId, true, 5)], T0.AddMinutes(6));
        string[] all = [AlertRuleCatalog.CpuRuleId, AlertRuleCatalog.DiskRuleId, AlertRuleCatalog.MemoryRuleId, AlertRulesConfig.PeerSilentRuleId];
        await EvaluateExpectingAsync(desktop, evaluator, "tight overrides", all);
        var active = await desktop.Alerts.ReadActiveAsync();
        Assert.Contains("70.0%", Assert.Single(active, alert => alert.RuleId == AlertRuleCatalog.CpuRuleId).Detail);
        var silent = Assert.Single(active, alert => alert.RuleId == AlertRulesConfig.PeerSilentRuleId);
        Assert.Equal(watched, silent.SubjectId);
        Assert.Contains("5 minutes", silent.Detail);

        // One peer's override never leaks to another peer: an override stored for an unrelated peer id changes nothing here.
        await desktop.Alerts.SetRuleOverridesAsync("peer-unrelated", [new(AlertRuleCatalog.CpuRuleId, false, null)], T0.AddMinutes(6));
        await EvaluateExpectingAsync(desktop, evaluator, "unrelated peer override", all);

        // Disabling a rule resolves its open alert instead of leaving it raised forever; the others are untouched.
        await desktop.Alerts.SetRuleOverridesAsync(local, [
            new(AlertRuleCatalog.CpuRuleId, false, null),
            new(AlertRuleCatalog.MemoryRuleId, true, 80),
            new(AlertRuleCatalog.DiskRuleId, true, 30)], T0.AddMinutes(7));
        await desktop.Alerts.SetRuleOverridesAsync(watched, [new(AlertRulesConfig.PeerSilentRuleId, false, 5)], T0.AddMinutes(7));
        await EvaluateExpectingAsync(desktop, evaluator, "cpu and peer-silent disabled",
            [AlertRuleCatalog.DiskRuleId, AlertRuleCatalog.MemoryRuleId]);

        // An empty list restores the built-in defaults for that peer: memory (85 <= 90) and disk (20 >= 10) resolve.
        await desktop.Alerts.SetRuleOverridesAsync(local, [], T0.AddMinutes(8));
        await EvaluateExpectingAsync(desktop, evaluator, "reset to defaults", []);
        Assert.DoesNotContain(await desktop.Alerts.ReadRuleOverridesAsync(), item => item.PeerId == local);

        // The effective rules a user sees reflect the stored overrides, the defaults and the accepted ranges.
        var rules = AlertRuleCatalog.Describe(Config(), local, [watched], await desktop.Alerts.ReadRuleOverridesAsync());
        var peerRule = Assert.Single(rules, rule => rule.RuleId == AlertRulesConfig.PeerSilentRuleId);
        Assert.False(peerRule.Enabled);
        Assert.True(peerRule.Overridden);
        Assert.Equal(5, peerRule.Threshold);
        Assert.Equal(10, peerRule.DefaultThreshold);
        Assert.All(rules.Where(rule => rule.Unit == "percent"), rule => Assert.False(rule.Overridden));
    }

    [Theory]
    [InlineData("threshold.cpu_high", 0.99, false)]
    [InlineData("threshold.cpu_high", 1, true)]
    [InlineData("threshold.cpu_high", 99, true)]
    [InlineData("threshold.cpu_high", 100, false)]
    [InlineData("threshold.memory_high", -5, false)]
    [InlineData("threshold.disk_free", 150, false)]
    [InlineData("threshold.disk_free", double.NaN, false)]
    [InlineData("threshold.disk_free", double.PositiveInfinity, false)]
    [InlineData("peer.silent", 0, false)]
    [InlineData("peer.silent", 1, true)]
    [InlineData("peer.silent", 10080, true)]
    [InlineData("peer.silent", 10081, false)]
    [InlineData("observation.unavailable", 5, false)]
    [InlineData("", 5, false)]
    public void RangesAndRuleKindsAreValidatedServerSide(string rule, double threshold, bool accepted)
    {
        var problem = AlertRuleCatalog.Validate([new(rule, true, threshold)]);
        Assert.True(accepted == (problem is null), $"{rule}={threshold}: expected accepted={accepted} but validation said '{problem ?? "ok"}'.");
    }

    [Fact]
    public async Task InvalidOverridesAreRejectedAtTheStoreAndNeverReplaceWhatWasSaved()
    {
        await using var desktop = await TestPeer.CreateAsync(T0);
        var local = desktop.PublicIdentity.PeerId;
        await desktop.Alerts.SetRuleOverridesAsync(local, [new(AlertRuleCatalog.CpuRuleId, true, 75)], T0);

        Assert.NotNull(AlertRuleCatalog.Validate([new(AlertRuleCatalog.CpuRuleId, true, 50), new(AlertRuleCatalog.CpuRuleId, true, 60)]));
        Assert.NotNull(AlertRuleCatalog.Validate([.. Enumerable.Range(1, 9).Select(index => new AlertRuleOverrideInput("threshold.cpu_high", true, index))]));
        await Assert.ThrowsAsync<ArgumentException>(() => desktop.Alerts.SetRuleOverridesAsync(
            local, [new(AlertRuleCatalog.CpuRuleId, true, 50), new(AlertRuleCatalog.DiskRuleId, true, 500)], T0.AddMinutes(1)));

        var stored = Assert.Single(await desktop.Alerts.ReadRuleOverridesAsync());
        Assert.Equal((AlertRuleCatalog.CpuRuleId, 75d), (stored.RuleId, stored.Threshold));

        // Rules apply only to a peer that can own them: signal rules to the owner itself, peer-not-seen to another active trusted peer.
        var activePeers = new HashSet<string> { "peer-b" };
        Assert.Null(AlertRuleCatalog.ValidateSubject(local, local, activePeers, [new(AlertRuleCatalog.CpuRuleId, true, 50)]));
        Assert.NotNull(AlertRuleCatalog.ValidateSubject("peer-b", local, activePeers, [new(AlertRuleCatalog.CpuRuleId, true, 50)]));
        Assert.Null(AlertRuleCatalog.ValidateSubject("peer-b", local, activePeers, [new(AlertRulesConfig.PeerSilentRuleId, true, 5)]));
        Assert.NotNull(AlertRuleCatalog.ValidateSubject("peer-stranger", local, activePeers, [new(AlertRulesConfig.PeerSilentRuleId, true, 5)]));
        Assert.NotNull(AlertRuleCatalog.ValidateSubject(local, local, activePeers, [new(AlertRulesConfig.PeerSilentRuleId, true, 5)]));
    }

    [Fact]
    public async Task SchemaV5MigratesToRuleOverridesKeepingExistingAlertData()
    {
        var path = Path.Combine(Path.GetTempPath(), "monitor-alert-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new AlertStore(AlertStoreOptions.Create(path));
        await store.InitializeAsync();
        var alert = new Alert(Alert.CreateId("peer-owner", "rule.x", "a", T0), 1, "rule.x", "a", "peer-owner",
            Contracts.V1.AlertSeverity.Warning, Contracts.V1.AlertState.Raised, "T", "D", T0, null);
        await store.RaiseAsync(alert, ["phone"], TimeSpan.FromDays(1), T0);
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE alert_rule_overrides; UPDATE alert_schema_version SET version = 5;";
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var migrated = new AlertStore(AlertStoreOptions.Create(path));
        await migrated.InitializeAsync();
        Assert.Empty(await migrated.ReadRuleOverridesAsync());
        Assert.Equal(alert.AlertId, Assert.Single(await migrated.ReadActiveAsync()).AlertId);
        await migrated.SetRuleOverridesAsync("peer-owner", [new(AlertRuleCatalog.DiskRuleId, true, 15.5)], T0);
        SqliteConnection.ClearAllPools();
        var reopened = new AlertStore(AlertStoreOptions.Create(path));
        await reopened.InitializeAsync();
        var kept = Assert.Single(await reopened.ReadRuleOverridesAsync());
        Assert.Equal(("peer-owner", AlertRuleCatalog.DiskRuleId, true, 15.5), (kept.PeerId, kept.RuleId, kept.Enabled, kept.Threshold));
    }
}
