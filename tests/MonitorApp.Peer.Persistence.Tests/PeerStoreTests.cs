using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
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
        var storedPeers = await restarted.ReadTrustedPeersAsync();
        Assert.Equal("peer-b", Assert.Single(storedPeers).PeerId);
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
    public async Task UpgradingFromSchemaTwoAddsObservedTimeIndexAndKeepsRecords()
    {
        var store = await CreateStoreAsync();
        await store.AppendOriginRecordAsync(ReplicatedRecord(1, now, [1]));
        var path = Path.Combine(directory, "peer.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DROP INDEX ix_origin_records_observed_at; DELETE FROM schema_migrations WHERE version = 3;";
            await downgrade.ExecuteNonQueryAsync();
        }

        var upgraded = await CreateStoreAsync();

        await using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        await check.OpenAsync();
        await using var versions = check.CreateCommand();
        versions.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        Assert.Equal(4L, (long)(await versions.ExecuteScalarAsync())!);
        await using var plan = check.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT sequence FROM origin_records WHERE observed_at >= 'a' AND observed_at < 'b' ORDER BY observed_at, origin_peer_id, origin_stream_id, sequence;";
        var detail = string.Empty;
        await using (var reader = await plan.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) detail += reader.GetString(3);
        }

        Assert.Contains("ix_origin_records_observed_at", detail, StringComparison.Ordinal);
        var page = await upgraded.ReadOriginWindowAsync(new OriginRecordWindowQuery(
            now.AddMinutes(-1), now.AddMinutes(1), null, null, null, null, 10));
        Assert.Equal(1UL, Assert.Single(page.Records).Sequence);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task WindowQueryIsHalfOpenOrderedAndRejectsInvalidBounds()
    {
        var store = await CreateStoreAsync();
        await store.AppendOriginRecordAsync(ReplicatedRecord(2, now.AddSeconds(10), [2]));
        await store.AppendOriginRecordAsync(ReplicatedRecord(1, now, [1]));
        await store.AppendOriginRecordAsync(ReplicatedRecord(3, now.AddSeconds(20), [3]));

        var page = await store.ReadOriginWindowAsync(new OriginRecordWindowQuery(
            now, now.AddSeconds(20), null, null, null, null, 10));
        var first = await store.ReadOriginWindowAsync(new OriginRecordWindowQuery(
            now, now.AddSeconds(30), null, null, null, null, 2));
        var rest = await store.ReadOriginWindowAsync(new OriginRecordWindowQuery(
            now, now.AddSeconds(30), null, null, null,
            new OriginRecordCursor(first.Records[^1].ObservedAt, "peer-b", "origin-b", first.Records[^1].Sequence), 2));

        Assert.Equal([1UL, 2UL], page.Records.Select(value => value.Sequence));
        Assert.True(first.Truncated);
        Assert.Equal([3UL], rest.Records.Select(value => value.Sequence));
        Assert.False(rest.Truncated);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadOriginWindowAsync(new OriginRecordWindowQuery(
            now, now, null, null, null, null, 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadOriginWindowAsync(new OriginRecordWindowQuery(
            now, now.AddSeconds(1), null, null, null, null, 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadOriginWindowAsync(new OriginRecordWindowQuery(
            now.ToOffset(TimeSpan.FromHours(2)), now.AddSeconds(1), null, null, null, null, 1)));
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task StateAsOfPicksLatestRecordPerSeriesWithinLookback()
    {
        var store = await CreateStoreAsync();
        await store.AppendOriginRecordAsync(ReplicatedRecord(1, now.AddMinutes(-30), [1]));
        await store.AppendOriginRecordAsync(ReplicatedRecord(2, now.AddMinutes(-5), [2]));
        await store.AppendOriginRecordAsync(ReplicatedRecord(3, now.AddMinutes(5), [3]));

        var inside = await store.ReadOriginStateAsOfAsync(now, now.AddHours(-1), null, null, 10);
        var narrow = await store.ReadOriginStateAsOfAsync(now, now.AddMinutes(-1), null, null, 10);

        Assert.Equal(2UL, Assert.Single(inside.Records).Sequence);
        Assert.Empty(narrow.Records);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadOriginStateAsOfAsync(now, now.AddSeconds(1), null, null, 10));
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
    public async Task CollectorReassignmentPreservesDeviceIdentityAttachmentAndOwnershipHistory()
    {
        var store = await CreateStoreAsync();
        await store.UpsertObservedEntityAsync(new ObservedEntity("ups-1", "ups", now));
        await store.UpsertObservedEntityAsync(new ObservedEntity("peer-a-usb", "peer-port", now));
        await store.UpsertObservedEntityAsync(new ObservedEntity("peer-b-usb", "peer-port", now));
        await store.AddIdentityClaimAsync(new IdentityClaim(
            "ups-1",
            "monitor.identity.serial",
            "UPS-ABC-123",
            IdentityStrength.Strong,
            "peer-a",
            RelationshipEvidenceKind.Reported,
            1,
            now));
        await store.UpsertRelationshipAsync(new RelationshipObservation(
            "attachment-a",
            "ups-1",
            "peer-a-usb",
            RelationshipKind.AttachedTo,
            RelationshipEvidenceKind.Reported,
            "peer-a",
            1,
            now,
            now.AddMinutes(5)));
        await store.UpsertRelationshipAsync(new RelationshipObservation(
            "attachment-b",
            "ups-1",
            "peer-b-usb",
            RelationshipKind.AttachedTo,
            RelationshipEvidenceKind.Reported,
            "peer-b",
            1,
            now.AddMinutes(5),
            null));
        await store.UpsertAdapterInstanceAsync(new AdapterInstance(
            "adapter-ups-a",
            "monitor.nut",
            "1.0.0",
            "peer-a",
            "ups-1",
            "secret://peer-a/ups",
            true));
        await store.UpsertAdapterInstanceAsync(new AdapterInstance(
            "adapter-ups-b",
            "monitor.nut",
            "1.0.0",
            "peer-b",
            "ups-1",
            "secret://peer-b/ups",
            true));

        await store.AssignCollectorAsync(new CollectorAssignment(
            "ups-1",
            "adapter-ups-a",
            "peer-a",
            now));
        await store.AssignCollectorAsync(new CollectorAssignment(
            "ups-1",
            "adapter-ups-b",
            "peer-b",
            now.AddMinutes(5)));

        var current = Assert.IsType<CollectorAssignment>(
            await store.ReadCollectorAssignmentAsync("ups-1"));
        Assert.Equal("peer-b", current.CollectorPeerId);
        var history = await store.ReadCollectorAssignmentHistoryAsync("ups-1");
        Assert.Collection(
            history,
            first =>
            {
                Assert.Equal("peer-a", first.CollectorPeerId);
                Assert.Equal(now.AddMinutes(5), first.ReleasedAt);
            },
            second =>
            {
                Assert.Equal("peer-b", second.CollectorPeerId);
                Assert.Null(second.ReleasedAt);
            });

        var identity = Assert.Single(await store.ReadIdentityClaimsAsync("ups-1"));
        Assert.Equal("UPS-ABC-123", identity.NormalizedValue);
        Assert.Equal(2, (await store.ReadRelationshipsAsync("ups-1")).Count);

        var restarted = await CreateStoreAsync();
        Assert.Equal(2, (await restarted.ReadCollectorAssignmentHistoryAsync("ups-1")).Count);
        Assert.Equal("UPS-ABC-123", Assert.Single(await restarted.ReadIdentityClaimsAsync("ups-1")).NormalizedValue);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StrongIdentityMatcherUsesStableClaimsAndNeverIpAddressAlone()
    {
        var left = new IdentityClaim(
            "device-left",
            "monitor.identity.serial",
            "SERIAL-1",
            IdentityStrength.Strong,
            "peer-a",
            RelationshipEvidenceKind.Reported,
            1,
            now);
        var right = left with { EntityId = "device-right", ObserverPeerId = "peer-b" };
        Assert.True(ObservedIdentityMatcher.ShareStrongIdentity([left], [right]));

        var leftIp = left with
        {
            ClaimType = "monitor.identity.ip_address",
            NormalizedValue = "192.0.2.10",
        };
        var rightIp = leftIp with { EntityId = "device-right", ObserverPeerId = "peer-b" };
        Assert.False(ObservedIdentityMatcher.ShareStrongIdentity([leftIp], [rightIp]));
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task CollectorAssignmentRejectsAdapterMismatchAndStrongIpIdentity()
    {
        var store = await CreateStoreAsync();
        await store.UpsertObservedEntityAsync(new ObservedEntity("ups-1", "ups", now));
        await store.UpsertObservedEntityAsync(new ObservedEntity("ups-2", "ups", now));
        await store.UpsertAdapterInstanceAsync(new AdapterInstance(
            "adapter-ups",
            "monitor.nut",
            "1.0.0",
            "peer-a",
            "ups-1",
            "secret://peer-a/ups",
            true));

        await Assert.ThrowsAsync<ArgumentException>(() => store.AssignCollectorAsync(
            new CollectorAssignment("ups-2", "adapter-ups", "peer-a", now)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AssignCollectorAsync(
            new CollectorAssignment("ups-1", "adapter-ups", "peer-b", now)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddIdentityClaimAsync(new IdentityClaim(
            "ups-1",
            "monitor.identity.ip_address",
            "192.0.2.10",
            IdentityStrength.Strong,
            "peer-a",
            RelationshipEvidenceKind.Reported,
            1,
            now)));
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

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task SchemaV3DatabaseMigratesToV4WithRetentionTablesAndKeepsData()
    {
        var store = await CreateStoreAsync();
        await store.SetLocalIdentityAsync(Identity());
        var bytes = new byte[] { 1, 2, 3 };
        await store.AppendOriginRecordAsync(LocalRecord(1, now, bytes));

        // Reduce the real file to what a v3 database holds: no retention tables, latest recorded version 3.
        var databasePath = Path.Combine(directory, "peer.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TABLE observation_rollups_hourly;
                DROP TABLE pinned_incidents;
                DROP TABLE pruned_origin_ranges;
                DELETE FROM schema_migrations WHERE version >= 4;
                INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES (3, '2026-11-01T00:00:00.0000000+00:00');
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var migrated = await CreateStoreAsync();

        Assert.Single(await migrated.ReadOriginRangeAsync("peer-a", "origin-a", 1, 10));
        await migrated.PinIncidentAsync(new PinnedIncident("inc-1", "outage", now, now.AddHours(1), now));
        Assert.Equal("inc-1", Assert.Single(await migrated.ReadPinnedIncidentsAsync()).IncidentId);
        Assert.Empty((await migrated.ReadHourlyRollupsAsync(new HourlyRollupQuery(now, now.AddHours(1), null, null, null, null, 10))).Rollups);
        Assert.Null(await migrated.ReadPrunedRangeContainingAsync("peer-a", "origin-a", 1));
        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await verify.OpenAsync();
        await using var versions = verify.CreateCommand();
        versions.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        Assert.Equal(4L, (long)(await versions.ExecuteScalarAsync())!);
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
