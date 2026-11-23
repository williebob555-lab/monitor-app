using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Replication;
using Xunit;

namespace MonitorApp.Peer.Replication.Tests;

/// <summary>
/// Mesh membership (T080): trust spreads through signed admit statements, removals spread and win, and nothing an
/// untrusted, removed or forging device says changes trust.
/// </summary>
public sealed class MeshMembershipTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "monitor-mesh-" + Guid.NewGuid().ToString("N"));
    private readonly List<Device> devices = [];

    [Fact]
    public async Task PairingOnOneDeviceMakesEveryMemberTrustTheNewDeviceAndRecordsWhoAddedIt()
    {
        var laptop = await DeviceAsync("laptop");
        var desktop = await DeviceAsync("desktop");
        var phone = await DeviceAsync("phone");
        await PairAsync(laptop, desktop);
        await PairAsync(desktop, phone);

        await ExchangeAsync(laptop, desktop);
        await ExchangeAsync(desktop, phone);

        Assert.Equal(PeerTrustState.Active, (await phone.Store.ReadTrustedPeerAsync(laptop.Id))?.TrustState);
        Assert.Equal(PeerTrustState.Active, (await laptop.Store.ReadTrustedPeerAsync(phone.Id))?.TrustState);
        var laptopOnPhone = (await phone.Store.ReadTrustedPeerAsync(laptop.Id))!;
        Assert.Equal(laptop.Identity.PublicKeyFingerprint, laptopOnPhone.PublicKeyFingerprint);
        Assert.Equal("laptop", laptopOnPhone.DisplayName);

        var mark = Assert.Single(await phone.Store.ReadMeshTrustMarksAsync());
        Assert.True(mark.AdmittedViaMesh);
        Assert.Equal(desktop.Id, mark.AdmittedBy);
        var history = (await phone.Store.ReadPeerStateTransitionsAsync(Now.AddDays(-1), Now.AddDays(1), 50)).Where(item => item.PeerId == laptop.Id).ToArray();
        Assert.Contains(history, item => item.Reason is { } reason && reason.Contains("Joined the mesh (added by desktop)", StringComparison.Ordinal));

        // The laptop's address travelled with the admission, so the phone can dial it.
        var locator = Assert.Single(await phone.Store.ReadPeerLocatorsAsync(laptop.Id));
        Assert.Equal((laptop.Host, 7443), (locator.Host, locator.Port));

        // A second exchange changes nothing.
        var version = phone.Mesh.Version;
        await ExchangeAsync(desktop, phone);
        await ExchangeAsync(laptop, phone);
        Assert.Equal(version, phone.Mesh.Version);
    }

    [Fact]
    public async Task StatementsFromUnknownIssuersForgedSignaturesTamperingAndFutureDatesAreNeverStored()
    {
        var phone = await DeviceAsync("phone");
        var desktop = await DeviceAsync("desktop");
        var stranger = await DeviceAsync("stranger");
        var victim = await DeviceAsync("victim");
        await PairAsync(phone, desktop);

        // An issuer the phone cannot reach (the stranger) vouches for itself through a third device.
        var unknownIssuer = Admit(stranger, victim, Now);
        // The desktop's id with someone else's key.
        var forged = Admit(stranger, victim, Now);
        forged.IssuerPeerId = desktop.Id;
        forged.IssuerPublicKeySha256 = ByteString.CopyFrom(desktop.Identity.PublicKeyFingerprint);
        // A genuine desktop statement altered after signing.
        var tampered = Admit(desktop, victim, Now);
        tampered.SubjectPublicKeySpki = ByteString.CopyFrom(stranger.Identity.PublicKeySpki);
        // A genuine desktop statement dated an hour ahead (a device about to be removed pre-dating its admissions).
        var future = Admit(desktop, victim, Now.AddHours(1));

        var result = await phone.Mesh.ApplyAsync(desktop.Id, [unknownIssuer, forged, tampered, future]);

        Assert.Equal(0, result.Stored);
        Assert.Equal(4, result.Ignored);
        Assert.Null(await phone.Store.ReadTrustedPeerAsync(victim.Id));
        Assert.Null(await phone.Store.ReadTrustedPeerAsync(stranger.Id));
        Assert.DoesNotContain(await phone.Store.ReadMeshStatementsAsync(), item => item.IssuerPeerId != phone.Id);

        // Only an actively trusted sender may hand over statements at all.
        await Assert.ThrowsAsync<OriginReplicationTrustException>(() => phone.Mesh.ApplyAsync(stranger.Id, [Admit(desktop, victim, Now)]));
    }

    [Fact]
    public async Task RemovalOnOneDeviceSpreadsIsFinalAndCannotBeUndoneByReAdmission()
    {
        var phone = await DeviceAsync("phone");
        var desktop = await DeviceAsync("desktop");
        var tablet = await DeviceAsync("tablet");
        await PairAsync(phone, desktop);
        await PairAsync(desktop, tablet);
        await ExchangeAsync(phone, desktop);
        await ExchangeAsync(desktop, tablet);
        Assert.Equal(PeerTrustState.Active, (await phone.Store.ReadTrustedPeerAsync(tablet.Id))?.TrustState);

        // The owner removes the tablet on the phone.
        await phone.Store.RevokeTrustedPeerAsync(tablet.Id, Now);
        await phone.Mesh.RefreshAsync();
        await ExchangeAsync(phone, desktop);

        Assert.Equal(PeerTrustState.Revoked, (await desktop.Store.ReadTrustedPeerAsync(tablet.Id))?.TrustState);
        var history = (await desktop.Store.ReadPeerStateTransitionsAsync(Now.AddDays(-1), Now.AddDays(1), 50)).Where(item => item.PeerId == tablet.Id).ToArray();
        Assert.Contains(history, item => item.Reason is { } reason && reason.Contains("Removed from the mesh by phone", StringComparison.Ordinal));

        // A newer admission (even from the desktop that paired it directly) does not bring it back anywhere.
        await phone.Mesh.ApplyAsync(desktop.Id, [Admit(desktop, tablet, Now.AddMinutes(5))]);
        await phone.Mesh.RefreshAsync();
        Assert.Equal(PeerTrustState.Revoked, (await phone.Store.ReadTrustedPeerAsync(tablet.Id))?.TrustState);
        var derivation = phone.Mesh.Derive((await AllStatementsAsync(phone)).ToArray());
        Assert.DoesNotContain(derivation.Trusted, item => item.PeerId == tablet.Id);
    }

    [Fact]
    public async Task ARemovedDeviceCannotKeepTrustOrAddDevicesEvenIfItRemovesOthersFirst()
    {
        var phone = await DeviceAsync("phone");
        var desktop = await DeviceAsync("desktop");
        var laptop = await DeviceAsync("laptop");
        var rogue = await DeviceAsync("rogue");
        await PairAsync(phone, desktop);
        await PairAsync(desktop, laptop);
        await PairAsync(laptop, rogue);
        await ExchangeAsync(laptop, rogue);
        await ExchangeAsync(desktop, laptop);
        await ExchangeAsync(phone, desktop);
        Assert.Equal(PeerTrustState.Active, (await phone.Store.ReadTrustedPeerAsync(rogue.Id))?.TrustState);

        // The rogue device (stolen, compromised) races: it removes the laptop and admits an attacker's device.
        var attacker = await DeviceAsync("attacker");
        var rogueStatements = new[] { Remove(rogue, laptop, Now.AddMinutes(1)), Admit(rogue, attacker, Now.AddMinutes(1)) };
        // The owner removes the rogue device on the laptop.
        await laptop.Store.RevokeTrustedPeerAsync(rogue.Id, Now.AddMinutes(2));
        await laptop.Mesh.RefreshAsync();
        var laptopStatements = await laptop.Mesh.GetStatementsForExchangeAsync();

        // The phone hears from both, in either order.
        await phone.Mesh.ApplyAsync(desktop.Id, rogueStatements.Concat(laptopStatements).ToArray());

        Assert.Equal(PeerTrustState.Revoked, (await phone.Store.ReadTrustedPeerAsync(rogue.Id))?.TrustState);
        Assert.Null(await phone.Store.ReadTrustedPeerAsync(attacker.Id));
        // The rogue's removal of the laptop is honoured (removal is never blocked), so the damage is loss of service only.
        Assert.Equal(PeerTrustState.Revoked, (await phone.Store.ReadTrustedPeerAsync(laptop.Id))?.TrustState);
        Assert.Equal(PeerTrustState.Active, (await phone.Store.ReadTrustedPeerAsync(desktop.Id))?.TrustState);
    }

    [Fact]
    public async Task AnAdmissionNeverRebindsAKnownPeerIdToAnotherKey()
    {
        var phone = await DeviceAsync("phone");
        var desktop = await DeviceAsync("desktop");
        var laptop = await DeviceAsync("laptop");
        var impostor = await DeviceAsync("impostor");
        await PairAsync(phone, desktop);
        await PairAsync(phone, laptop);

        // The desktop vouches for "the laptop" with the impostor's key.
        var claim = MeshStatementCryptography.CreateSigned(desktop.Key, desktop.Identity, MeshStatementKind.Admit,
            new PeerPublicIdentity(laptop.Id, impostor.Identity.PublicKeySpki, impostor.Identity.PublicKeyFingerprint),
            "laptop", [], Now);
        await phone.Mesh.ApplyAsync(desktop.Id, [claim]);

        var stored = (await phone.Store.ReadTrustedPeerAsync(laptop.Id))!;
        Assert.Equal(laptop.Identity.PublicKeyFingerprint, stored.PublicKeyFingerprint);
        Assert.Empty(await phone.Store.ReadMeshTrustMarksAsync());
    }

    [Fact]
    public async Task DerivedTrustDoesNotDependOnTheOrderStatementsArrive()
    {
        var a = await DeviceAsync("a");
        var b = await DeviceAsync("b");
        var c = await DeviceAsync("c");
        var d = await DeviceAsync("d");
        var e = await DeviceAsync("e");
        var statements = new List<MeshStatement>
        {
            Admit(a, b, Now), Admit(b, c, Now), Admit(c, d, Now), Admit(b, e, Now), Admit(e, d, Now),
            Remove(b, c, Now.AddMinutes(1)), Remove(c, e, Now.AddMinutes(1)),
        };
        string Summary(MeshDerivation derivation) =>
            string.Join(",", derivation.Trusted.Select(item => item.PeerId).Order(StringComparer.Ordinal)) + "|" +
            string.Join(",", derivation.Removed.Select(item => item.Subject.PeerId).Order(StringComparer.Ordinal));

        var expected = Summary(MeshMembership.Derive(a.Identity, statements));
        var random = new Random(23);
        for (var round = 0; round < 25; round++)
        {
            Assert.Equal(expected, Summary(MeshMembership.Derive(a.Identity, statements.OrderBy(_ => random.Next()).ToArray())));
        }

        // b vouches for c and e; c and e are both removed; d was reachable only through them.
        Assert.Equal($"{b.Id}|{c.Id},{e.Id}".Split('|')[0], expected.Split('|')[0]);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var device in devices) device.Key.Dispose();
        await Task.Yield();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private async Task<Device> DeviceAsync(string name)
    {
        Directory.CreateDirectory(directory);
        var key = EcdsaPeerIdentityKey.Create();
        var identity = PeerPublicIdentity.Create(name + "-" + Guid.NewGuid().ToString("N")[..8], key);
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N") + ".db")));
        await store.InitializeAsync();
        var device = new Device(name, key, identity, store, new MeshMembership(store, identity, key, () => Now), $"100.64.0.{devices.Count + 1}");
        devices.Add(device);
        return device;
    }

    private static async Task PairAsync(Device first, Device second)
    {
        await first.Store.UpsertTrustedPeerAsync(new TrustedPeer(second.Id, second.Identity.PublicKeySpki,
            second.Identity.PublicKeyFingerprint, second.Name, PeerTrustState.Active, Now, null));
        await second.Store.UpsertTrustedPeerAsync(new TrustedPeer(first.Id, first.Identity.PublicKeySpki,
            first.Identity.PublicKeyFingerprint, first.Name, PeerTrustState.Active, Now, null));
        // Every device in these tests listens on its own address.
        await first.Store.UpsertPeerLocatorAsync(new PeerLocator(second.Id, second.Host, 7443, Now, null));
        await second.Store.UpsertPeerLocatorAsync(new PeerLocator(first.Id, first.Host, 7443, Now, null));
    }

    /// <summary>What one authenticated session does: both sides send their statements and apply the other's.</summary>
    private static async Task ExchangeAsync(Device first, Device second)
    {
        var fromFirst = await first.Mesh.GetStatementsForExchangeAsync();
        var fromSecond = await second.Mesh.GetStatementsForExchangeAsync();
        await second.Mesh.ApplyAsync(first.Id, fromFirst);
        await first.Mesh.ApplyAsync(second.Id, fromSecond);
    }

    private static async Task<IReadOnlyList<MeshStatement>> AllStatementsAsync(Device device) =>
        (await device.Store.ReadMeshStatementsAsync()).Select(item => MeshStatement.Parser.ParseFrom(item.StatementBytes)).ToArray();

    private static MeshStatement Admit(Device issuer, Device subject, DateTimeOffset at) =>
        MeshStatementCryptography.CreateSigned(issuer.Key, issuer.Identity, MeshStatementKind.Admit, subject.Identity, subject.Name,
            [new MeshLocator { Host = "100.64.0.9", Port = 7443 }], at);

    private static MeshStatement Remove(Device issuer, Device subject, DateTimeOffset at) =>
        MeshStatementCryptography.CreateSigned(issuer.Key, issuer.Identity, MeshStatementKind.Remove, subject.Identity, subject.Name, [], at);

    private sealed record Device(string Name, EcdsaPeerIdentityKey Key, PeerPublicIdentity Identity, PeerStore Store, MeshMembership Mesh, string Host)
    {
        public string Id => Identity.PeerId;
    }
}
