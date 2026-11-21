using System.Security.Cryptography;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Persistence.Tests;

public sealed class PeerStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "MonitorApp.Peer.Persistence.Tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset now = new(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task IdentityTrustLocatorAndPairingSurviveRestart()
    {
        var store = await CreateStoreAsync();
        var identity = Identity();
        await store.SetLocalIdentityAsync(identity);
        await store.SetLocalIdentityAsync(identity);

        var remoteIdentity = CreateIdentityMaterial();
        var remote = new TrustedPeer("peer-b", remoteIdentity.PublicKey, remoteIdentity.Fingerprint, "Peer B", PeerTrustState.Active, now, null);
        await store.UpsertTrustedPeerAsync(remote);
        await store.UpsertPeerLocatorAsync(new PeerLocator("peer-b", "100.64.0.2", 44044, now, null));
        await store.CreatePairingSessionAsync(new PairingSession("pair-1", "peer-b", now, now.AddMinutes(2), false));
        await store.CompletePairingSessionAsync("pair-1", now.AddSeconds(10));
        await store.RevokeTrustedPeerAsync("peer-b", now.AddMinutes(1));

        var restarted = await CreateStoreAsync();
        AssertIdentity(identity, Assert.IsType<LocalPeerIdentity>(await restarted.ReadLocalIdentityAsync()));
        var storedPeer = Assert.IsType<TrustedPeer>(await restarted.ReadTrustedPeerAsync("peer-b"));
        Assert.Equal(PeerTrustState.Revoked, storedPeer.TrustState);
        Assert.Equal(now.AddMinutes(1), storedPeer.RevokedAt);
        var locator = Assert.Single(await restarted.ReadPeerLocatorsAsync("peer-b"));
        Assert.Equal("100.64.0.2", locator.Host);

        var otherIdentity = CreateIdentityMaterial();
        await Assert.ThrowsAsync<PeerPersistenceConflictException>(() => restarted.UpsertTrustedPeerAsync(
            remote with { PublicKeySpki = otherIdentity.PublicKey, PublicKeyFingerprint = otherIdentity.Fingerprint }));
        await Assert.ThrowsAsync<PeerPersistenceConflictException>(() => restarted.CompletePairingSessionAsync("pair-1", now.AddSeconds(20)));
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task LocalOriginSequenceIsContiguousAndRetriesAreByteIdentical()
    {
        var store = await CreateStoreAsync();
        await store.SetLocalIdentityAsync(Identity());
        var first = LocalRecord(1, now, [1, 2, 3]);
        var second = LocalRecord(2, now.AddSeconds(1), [4, 5, 6]);

        Assert.Equal(RecordInsertOutcome.Inserted, await store.AppendOriginRecordAsync(first));
        Assert.Equal(RecordInsertOutcome.IdenticalRetry, await store.AppendOriginRecordAsync(first));
        await Assert.ThrowsAsync<PeerPersistenceConflictException>(() => store.AppendOriginRecordAsync(first with { RecordBytes = [9] }));
        await Assert.ThrowsAsync<PeerPersistenceConflictException>(() => store.AppendOriginRecordAsync(LocalRecord(3, now, [7])));
        Assert.Equal(RecordInsertOutcome.Inserted, await store.AppendOriginRecordAsync(second));

        var restarted = await CreateStoreAsync();
        var range = await restarted.ReadOriginRangeAsync("peer-a", "origin-a", 1, 10);
        Assert.Equal([1UL, 2UL], range.Select(value => value.Sequence));
        Assert.Equal(SHA256.HashData(first.RecordBytes), range[0].ContentHash);
        var current = Assert.Single(await restarted.ReadCurrentObservationsAsync());
        Assert.Equal(2UL, current.Sequence);
        Assert.Equal(second.RecordBytes, current.RecordBytes);
        Assert.Equal(3UL, (await restarted.ReadLocalIdentityAsync())!.NextSequence);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task ReplicatedRecordsPermitGapsButRejectConflictingSequences()
    {
        var store = await CreateStoreAsync();
        var fifth = ReplicatedRecord(5, now.AddSeconds(5), [5]);
        var third = ReplicatedRecord(3, now.AddSeconds(3), [3]);
        await store.AppendOriginRecordAsync(fifth);
        await store.AppendOriginRecordAsync(third);
        Assert.Equal(RecordInsertOutcome.IdenticalRetry, await store.AppendOriginRecordAsync(fifth));
        await Assert.ThrowsAsync<PeerPersistenceConflictException>(() => store.AppendOriginRecordAsync(fifth with { Quality = "error" }));

        var range = await store.ReadOriginRangeAsync("peer-b", "origin-b", 1, 10);
        Assert.Equal([3UL, 5UL], range.Select(value => value.Sequence));
        var current = Assert.Single(await store.ReadCurrentObservationsAsync());
        Assert.Equal(5UL, current.Sequence);
        Assert.Equal(RecordProvenance.Replicated, current.Provenance);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task ReplicationCursorAndGapsAreDurableAndCannotMoveBackwards()
    {
        var store = await CreateStoreAsync();
        var cursor = new ReplicationCursor("peer-b", "peer-c", "origin-c", 4, "raw", now);
        await store.UpdateReplicationCursorAsync(cursor, [new ReplicationGap(6, 7), new ReplicationGap(9, 9)]);
        await store.UpdateReplicationCursorAsync(cursor with { AcceptedThrough = 5, UpdatedAt = now.AddSeconds(1) }, [new ReplicationGap(7, 7)]);
        await Assert.ThrowsAsync<PeerPersistenceConflictException>(() => store.UpdateReplicationCursorAsync(cursor with { AcceptedThrough = 3 }, []));

        var restarted = await CreateStoreAsync();
        var state = await restarted.ReadReplicationCursorAsync("peer-b", "peer-c", "origin-c");
        Assert.Equal(5UL, state.Cursor!.AcceptedThrough);
        Assert.Equal(new ReplicationGap(7, 7), Assert.Single(state.Gaps));
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.UpdateReplicationCursorAsync(
            cursor with { AcceptedThrough = 5 }, [new ReplicationGap(5, 6)]));
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task ObservedIdentityRelationshipsAndCollectorAssignmentPreserveEvidence()
    {
        var store = await CreateStoreAsync();
        await store.UpsertObservedEntityAsync(new ObservedEntity("switch-1", "managed-switch", now));
        await store.UpsertObservedEntityAsync(new ObservedEntity("peer-port", "switch-port", now));
        await store.AddIdentityClaimAsync(new IdentityClaim("switch-1", "serial", "ABC123", IdentityStrength.Strong,
            "peer-a", RelationshipEvidenceKind.Reported, 0.99, now));
        await store.UpsertRelationshipAsync(new RelationshipObservation("rel-1", "peer-port", "switch-1",
            RelationshipKind.AttachedTo, RelationshipEvidenceKind.Reported, "peer-a", 0.9, now, null));
        await store.UpsertAdapterInstanceAsync(new AdapterInstance("adapter-1", "monitor.snmp", "1.0.0", "peer-a", "switch-1", "secret://snmp/switch-1", true));
        await store.AssignCollectorAsync(new CollectorAssignment("switch-1", "adapter-1", "peer-a", now));

        var claim = Assert.Single(await store.ReadIdentityClaimsAsync("switch-1"));
        Assert.Equal(IdentityStrength.Strong, claim.Strength);
        Assert.Equal(RelationshipEvidenceKind.Reported, claim.EvidenceKind);
        var relationship = Assert.Single(await store.ReadRelationshipsAsync("switch-1"));
        Assert.Equal("peer-port", relationship.SourceEntityId);
        var assignment = Assert.IsType<CollectorAssignment>(await store.ReadCollectorAssignmentAsync("switch-1"));
        Assert.Equal("peer-a", assignment.CollectorPeerId);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task ReplicationDescriptorNeverContainsLocalCredentialReference()
    {
        var store = await CreateStoreAsync();
        await store.UpsertObservedEntityAsync(new ObservedEntity("ups-1", "ups", now));
        await store.UpsertAdapterInstanceAsync(new AdapterInstance("adapter-ups", "monitor.nut", "1.0.0", "peer-a", "ups-1", "secret://nut/password", true));
        await store.RecordAdapterStatusAsync(new AdapterStatus("adapter-ups", "healthy", null, now));
        await store.UpsertCapabilityAsync(new CapabilityCatalogEntry("monitor.nut.ups", "monitor.nut", "1.0.0", [1, 2], now));

        var descriptor = Assert.IsType<AdapterReplicationDescriptor>(await store.ReadAdapterReplicationDescriptorAsync("adapter-ups"));
        Assert.DoesNotContain("Credential", descriptor.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", descriptor.ToString(), StringComparison.OrdinalIgnoreCase);
        var status = Assert.IsType<AdapterStatus>(await store.ReadLatestAdapterStatusAsync("adapter-ups"));
        Assert.Equal("healthy", status.State);
        var capability = Assert.Single(await store.ReadCapabilitiesAsync());
        Assert.Equal("monitor.nut", capability.Namespace);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NonUtcTimestampsFailBeforePersistence()
    {
        var store = await CreateStoreAsync();
        var local = now.ToOffset(TimeSpan.FromHours(-5));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetLocalIdentityAsync(Identity() with { CreatedAt = local }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendOriginRecordAsync(ReplicatedRecord(1, local, [1])));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CryptographicIdentityEnumPortAndSignatureShapesFailClosed()
    {
        var store = await CreateStoreAsync();
        var identity = Identity();
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetLocalIdentityAsync(identity with { PublicKeySpki = [1, 2, 3], PublicKeyFingerprint = SHA256.HashData([1, 2, 3]) }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetLocalIdentityAsync(identity with { PublicKeyFingerprint = new byte[32] }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpsertTrustedPeerAsync(new TrustedPeer(
            "peer-b", identity.PublicKeySpki, identity.PublicKeyFingerprint, null, (PeerTrustState)999, now, null)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpsertPeerLocatorAsync(new PeerLocator("peer-b", "100.64.0.2", 0, now, null)));
        await store.SetLocalIdentityAsync(identity);
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendOriginRecordAsync(LocalRecord(1, now, [1]) with { Signature = [1, 2] }));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private async Task<PeerStore> CreateStoreAsync()
    {
        Directory.CreateDirectory(directory);
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "peer.db")));
        await store.InitializeAsync();
        return store;
    }

    private LocalPeerIdentity Identity()
    {
        var identity = CreateIdentityMaterial();
        return new LocalPeerIdentity("peer-a", identity.PublicKey, identity.Fingerprint, "keystore://peer-a", "origin-a", 1, now);
    }

    private static OriginRecordWrite LocalRecord(ulong sequence, DateTimeOffset observedAt, byte[] bytes) => new(
        "peer-a", "origin-a", sequence, "host-a", "system", "cpu", bytes, Sign(bytes), observedAt,
        observedAt.AddMilliseconds(10), null, RecordProvenance.LocalOrigin, "fresh");

    private static OriginRecordWrite ReplicatedRecord(ulong sequence, DateTimeOffset observedAt, byte[] bytes) => new(
        "peer-b", "origin-b", sequence, "host-b", "system", "cpu", bytes, Sign(bytes), observedAt,
        observedAt.AddMilliseconds(10), "peer-b", RecordProvenance.Replicated, "fresh");

    private static (byte[] PublicKey, byte[] Fingerprint) CreateIdentityMaterial()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        return (publicKey, SHA256.HashData(publicKey));
    }

    private static byte[] Sign(byte[] bytes)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static void AssertIdentity(LocalPeerIdentity expected, LocalPeerIdentity actual)
    {
        Assert.Equal(expected.PeerId, actual.PeerId);
        Assert.Equal(expected.PublicKeySpki, actual.PublicKeySpki);
        Assert.Equal(expected.PublicKeyFingerprint, actual.PublicKeyFingerprint);
        Assert.Equal(expected.KeyProviderReference, actual.KeyProviderReference);
        Assert.Equal(expected.OriginStreamId, actual.OriginStreamId);
        Assert.Equal(expected.NextSequence, actual.NextSequence);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
    }
}
