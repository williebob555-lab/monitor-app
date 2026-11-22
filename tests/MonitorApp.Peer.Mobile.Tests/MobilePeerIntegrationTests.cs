using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Mobile;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Mobile.Tests;

public sealed class MobilePeerIntegrationTests
{
    private static MobilePeerOptions FastOptions(string dir) => new(
        dir, "test-phone", TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));

    [Fact]
    public async Task PhoneInitiatedPairingThenOfflineCollectionReconnectConvergesBothWaysAndRevocationIsRefused()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var phoneIdentity = new SoftwareIdentity();
            var reader = new ScriptedDeviceReader();
            await using var phone = await MobilePeerRuntime.CreateAsync(FastOptions(phoneRoot), phoneIdentity, reader);

            // Phone ingests while it has no trusted peer at all.
            Assert.Equal(6, (await phone.CollectOnceAsync()).AppendedRecords);
            await phone.CollectOnceAsync();
            await desktop.CollectAsync();

            // Phone initiates; desktop explicitly approves.
            var pairTask = phone.PairAsync(desktop.Invitation(), "pairing-integration-1");
            await desktop.ApproveWhenPendingAsync("pairing-integration-1");
            Assert.Equal(desktop.Identity.PeerId, await pairTask.WaitAsync(TimeSpan.FromSeconds(30)));

            var phoneTrustOfDesktop = await phone.Store.ReadTrustedPeerAsync(desktop.Identity.PeerId);
            var desktopTrustOfPhone = await desktop.Store.ReadTrustedPeerAsync(phone.Identity.PeerId);
            Assert.Equal(PeerTrustState.Active, phoneTrustOfDesktop?.TrustState);
            Assert.Equal(PeerTrustState.Active, desktopTrustOfPhone?.TrustState);
            Assert.Equal(phone.Identity.PublicKeyFingerprint, desktopTrustOfPhone!.PublicKeyFingerprint);

            // First phone-initiated session: both directions over the one outbound connection.
            var first = Assert.Single(await phone.SyncOnceAsync());
            Assert.Equal(MobileSyncState.Synced, first.State);
            await AssertConvergedAsync(phone, desktop);

            // Desktop goes away; phone and desktop both keep collecting; sync reports offline.
            await desktop.StopHostAsync();
            await phone.CollectOnceAsync();
            await desktop.CollectAsync();
            Assert.Equal(MobileSyncState.Offline, Assert.Single(await phone.SyncOnceAsync()).State);
            Assert.NotNull(phone.GetStatus().Peers[0].LastSuccess);

            // Reconnect: both histories converge with no duplicates, and a repeat session is idempotent.
            await desktop.StartHostAsync();
            await Task.Delay(300); // exceed the capped backoff
            Assert.Equal(MobileSyncState.Synced, Assert.Single(await phone.SyncOnceAsync()).State);
            var counts = await AssertConvergedAsync(phone, desktop);
            Assert.Equal(3 * 6, counts.PhoneOrigin);
            Assert.Equal(MobileSyncState.Synced, Assert.Single(await phone.SyncOnceAsync()).State);
            Assert.Equal(counts, await AssertConvergedAsync(phone, desktop));

            // Phone-origin records on the desktop keep their provenance, units and quality.
            var phoneLocal = await phone.Store.ReadLocalIdentityAsync();
            var replicated = await desktop.Store.ReadOriginRangeAsync(phoneLocal!.PeerId, phoneLocal.OriginStreamId, 1, 100);
            Assert.All(replicated, record => Assert.Equal(RecordProvenance.Replicated, record.Provenance));
            var battery = replicated.First(record => record.SignalId == AndroidDeviceSnapshotCollector.BatteryLevel);
            var envelope = TelemetryEnvelope.Parser.ParseFrom(battery.RecordBytes);
            Assert.Equal(MetricUnit.Ratio, envelope.Metric.Unit);
            Assert.Equal(0.8, envelope.Metric.Value.DoubleValue, 3);

            // Desktop revokes the phone: new phone data is refused and never reaches the desktop.
            var desktopCountBefore = counts.PhoneOrigin;
            await desktop.Store.RevokeTrustedPeerAsync(phone.Identity.PeerId, DateTimeOffset.UtcNow);
            await phone.CollectOnceAsync();
            await Task.Delay(300);
            var refused = Assert.Single(await phone.SyncOnceAsync());
            Assert.Equal(MobileSyncState.Refused, refused.State);
            var afterRevoke = await desktop.Store.ReadOriginRangeAsync(phoneLocal.PeerId, phoneLocal.OriginStreamId, 1, 1000);
            Assert.Equal(desktopCountBefore, afterRevoke.Count);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task StartedRuntimeCollectsAndSyncsWithoutManualCallsUntilStopped()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(
                FastOptions(phoneRoot), phoneIdentity, new ScriptedDeviceReader());
            var pairTask = phone.PairAsync(desktop.Invitation(), "pairing-integration-2");
            await desktop.ApproveWhenPendingAsync("pairing-integration-2");
            await pairTask.WaitAsync(TimeSpan.FromSeconds(30));

            phone.Start();
            Assert.True(phone.GetStatus().Running);
            var local = await phone.Store.ReadLocalIdentityAsync();
            await WaitAsync(async () =>
                (await desktop.Store.ReadOriginRangeAsync(local!.PeerId, local.OriginStreamId, 1, 100)).Count > 0);
            await phone.StopAsync();
            Assert.False(phone.GetStatus().Running);

            var settled = (await phone.Store.ReadLocalIdentityAsync())!.NextSequence;
            await Task.Delay(400);
            Assert.Equal(settled, (await phone.Store.ReadLocalIdentityAsync())!.NextSequence);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    [Fact]
    public async Task PairingWithWrongPinnedFingerprintIsRejectedAndNothingIsTrusted()
    {
        var desktopRoot = TestDirectories.Create();
        var phoneRoot = TestDirectories.Create();
        try
        {
            await using var desktop = await DesktopPeer.StartAsync(desktopRoot);
            using var phoneIdentity = new SoftwareIdentity();
            await using var phone = await MobilePeerRuntime.CreateAsync(
                FastOptions(phoneRoot), phoneIdentity, new ScriptedDeviceReader());
            var real = desktop.Invitation();
            var forged = new MonitorApp.Peer.Transport.PeerPairingInvitation(
                real.Address, real.Port, real.PeerId, new byte[32], null);
            await Assert.ThrowsAsync<MobilePairingFailedException>(() => phone.PairAsync(forged, "pairing-integration-3"));
            Assert.Empty(await phone.Store.ReadTrustedPeersAsync());
            Assert.Null(phone.GetStatus().PendingPairingId);
        }
        finally
        {
            Cleanup(desktopRoot, phoneRoot);
        }
    }

    private static async Task<(int PhoneOrigin, int DesktopOrigin)> AssertConvergedAsync(MobilePeerRuntime phone, DesktopPeer desktop)
    {
        var phoneLocal = (await phone.Store.ReadLocalIdentityAsync())!;
        var desktopLocal = (await desktop.Store.ReadLocalIdentityAsync())!;
        var phoneOnPhone = await phone.Store.ReadOriginRangeAsync(phoneLocal.PeerId, phoneLocal.OriginStreamId, 1, 10_000);
        var phoneOnDesktop = await desktop.Store.ReadOriginRangeAsync(phoneLocal.PeerId, phoneLocal.OriginStreamId, 1, 10_000);
        var desktopOnDesktop = await desktop.Store.ReadOriginRangeAsync(desktopLocal.PeerId, desktopLocal.OriginStreamId, 1, 10_000);
        var desktopOnPhone = await phone.Store.ReadOriginRangeAsync(desktopLocal.PeerId, desktopLocal.OriginStreamId, 1, 10_000);
        Assert.NotEmpty(phoneOnPhone);
        Assert.NotEmpty(desktopOnDesktop);
        Assert.Equal(phoneOnPhone.Select(r => (r.Sequence, r.ContentHash.Length, Convert.ToHexString(r.ContentHash))),
            phoneOnDesktop.Select(r => (r.Sequence, r.ContentHash.Length, Convert.ToHexString(r.ContentHash))));
        Assert.Equal(desktopOnDesktop.Select(r => (r.Sequence, Convert.ToHexString(r.ContentHash))),
            desktopOnPhone.Select(r => (r.Sequence, Convert.ToHexString(r.ContentHash))));
        Assert.Equal(phoneOnDesktop.Count, phoneOnDesktop.Select(r => r.Sequence).Distinct().Count());
        Assert.Equal(desktopOnPhone.Count, desktopOnPhone.Select(r => r.Sequence).Distinct().Count());
        return (phoneOnDesktop.Count, desktopOnPhone.Count);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }

        Assert.Fail("Condition was not met in time.");
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
