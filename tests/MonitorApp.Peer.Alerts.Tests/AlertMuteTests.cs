using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Alerts;
using Xunit;

namespace MonitorApp.Peer.Alerts.Tests;

public sealed class AlertMuteTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    private static Alert NewAlert(string rule) => new(
        Alert.CreateId("peer-owner", rule, "s", T0), 1, rule, "s", "peer-owner",
        AlertSeverity.Warning, AlertState.Raised, "Title", "Detail", T0, null);

    private static async Task<AlertStore> StoreAsync()
    {
        var store = new AlertStore(AlertStoreOptions.Create(
            Path.Combine(Path.GetTempPath(), "monitor-alert-mute-" + Guid.NewGuid().ToString("N") + ".db")));
        await store.InitializeAsync();
        return store;
    }

    [Fact]
    public async Task MutedRulesAreHeldBackThenDeliveredWhenTheMuteLapsesOrIsLifted()
    {
        var store = await StoreAsync();
        var disk = NewAlert("rule.disk");
        var memory = NewAlert("rule.memory");
        await store.RaiseAsync(disk, ["phone"], Ttl, T0);
        await store.RaiseAsync(memory, ["phone"], Ttl, T0);

        await store.MuteAsync("phone", "rule.disk", T0.AddHours(1), T0);
        Assert.Equal(memory.AlertId, Assert.Single((await store.ReadPendingAsync("phone", T0.AddMinutes(1), 10)).Alerts).AlertId);
        Assert.Single(await store.ReadActiveMutesAsync(T0.AddMinutes(1)));

        // The mute lapses on its own at its expiry.
        Assert.Equal(2, (await store.ReadPendingAsync("phone", T0.AddHours(1), 10)).Alerts.Count);
        Assert.Empty(await store.ReadActiveMutesAsync(T0.AddHours(1)));

        // A mute of every rule holds back everything, and lifting it wakes live sessions.
        await store.MuteAsync("phone", "*", T0.AddHours(5), T0.AddHours(2));
        Assert.Empty((await store.ReadPendingAsync("phone", T0.AddHours(3), 10)).Alerts);
        var woken = new List<string>();
        store.OutboxEnqueued += recipients => woken.AddRange(recipients);
        await store.RegisterRecipientAsync("phone", T0, Ttl);
        woken.Clear();
        Assert.True(await store.UnmuteAsync("phone", "*"));
        Assert.Contains("phone", woken);
        Assert.False(await store.UnmuteAsync("phone", "*"));
        Assert.Equal(2, (await store.ReadPendingAsync("phone", T0.AddHours(3), 10)).Alerts.Count);
    }

    [Fact]
    public async Task OperatorAcknowledgementIsIdempotentAndOnlyForKnownAlerts()
    {
        var store = await StoreAsync();
        var alert = NewAlert("rule.disk");
        await store.RaiseAsync(alert, ["phone"], Ttl, T0);
        Assert.False(await store.AcknowledgeByOperatorAsync("unknown", "phone", T0));
        Assert.True(await store.AcknowledgeByOperatorAsync(alert.AlertId, "phone", T0));
        Assert.True(await store.AcknowledgeByOperatorAsync(alert.AlertId, "other", T0.AddMinutes(1)));
        var (id, by, at) = Assert.Single(await store.ReadOperatorAcknowledgementsAsync());
        Assert.Equal((alert.AlertId, "phone", T0), (id, by, at));
    }
}
