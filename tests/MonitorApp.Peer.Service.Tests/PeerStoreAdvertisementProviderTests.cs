using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Service;
using Xunit;

namespace MonitorApp.Peer.Service.Tests;

public sealed class PeerStoreAdvertisementProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshLocalStreamAndCapabilitiesAdvertiseNoPrivateMaterial()
    {
        await using var fixture = await Fixture.CreateAsync();
        var secretDescriptor = Encoding.UTF8.GetBytes("credential=top-secret;private-key=never-export");
        await fixture.Store.UpsertCapabilityAsync(new CapabilityCatalogEntry(
            "system",
            "monitor",
            "1.0.0",
            secretDescriptor,
            Now));
        var provider = fixture.Provider();

        var capabilities = await provider.GetCapabilitiesAsync(CancellationToken.None);
        var cursors = await provider.GetCursorsAsync(RemoteHello(), CancellationToken.None);

        var capability = Assert.Single(capabilities);
        Assert.Equal("monitor.system", capability.ModuleId);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(secretDescriptor), capability.DeclarationSha256.ToByteArray());
        var cursor = Assert.Single(cursors);
        Assert.Equal("peer-local", cursor.OriginPeerId);
        Assert.Equal("stream-local", cursor.OriginStreamId);
        Assert.Equal<ulong>(0, cursor.AcceptedThroughSequence);
        Assert.Equal(Now, cursor.UpdatedAt.ToDateTimeOffset());
        var advertised = new PeerHello { Capabilities = { capabilities }, Cursors = { cursors } }.ToByteArray();
        var text = Encoding.UTF8.GetString(advertised);
        Assert.DoesNotContain("top-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("never-export", text, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-provider-private", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdvancedLocalHeadAndKnownRemoteCursorAreTruthfulAndStable()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AppendLocalAsync(1);
        await fixture.AppendLocalAsync(2);
        await fixture.Store.UpdateReplicationCursorAsync(
            new ReplicationCursor(
                "peer-remote",
                "peer-remote",
                "stream-remote",
                7,
                "raw",
                Now.AddMinutes(-1)),
            []);
        var remote = RemoteHello();
        remote.Cursors.Add(new OriginCursor
        {
            OriginPeerId = "peer-remote",
            OriginStreamId = "stream-remote",
            AcceptedThroughSequence = 10,
            UpdatedAt = Timestamp.FromDateTimeOffset(Now),
        });
        remote.Cursors.Add(new OriginCursor
        {
            OriginPeerId = "unrelated-peer",
            OriginStreamId = "not-disclosed",
            AcceptedThroughSequence = 99,
            UpdatedAt = Timestamp.FromDateTimeOffset(Now),
        });

        var cursors = await fixture.Provider().GetCursorsAsync(remote, CancellationToken.None);

        Assert.Equal(2, cursors.Count);
        Assert.Collection(
            cursors,
            local =>
            {
                Assert.Equal("peer-local", local.OriginPeerId);
                Assert.Equal<ulong>(2, local.AcceptedThroughSequence);
            },
            retained =>
            {
                Assert.Equal("peer-remote", retained.OriginPeerId);
                Assert.Equal("stream-remote", retained.OriginStreamId);
                Assert.Equal<ulong>(7, retained.AcceptedThroughSequence);
                Assert.Equal(Now.AddMinutes(-1), retained.UpdatedAt.ToDateTimeOffset());
            });
        Assert.DoesNotContain(cursors, cursor => cursor.OriginStreamId == "not-disclosed");
    }

    [Fact]
    public async Task CapabilityOutputIsBoundedAndOrdinallyStable()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddCapabilityAsync("zeta", "monitor.zeta");
        await fixture.AddCapabilityAsync("alpha", "monitor");
        await fixture.AddCapabilityAsync("beta", "monitor");
        var provider = fixture.Provider(maximumCapabilities: 2);

        var first = await provider.GetCapabilitiesAsync(CancellationToken.None);
        var second = await provider.GetCapabilitiesAsync(CancellationToken.None);

        Assert.Equal(["monitor.alpha", "monitor.beta"], first.Select(item => item.ModuleId));
        Assert.Equal(
            first.Select(item => Convert.ToHexString(item.ToByteArray())),
            second.Select(item => Convert.ToHexString(item.ToByteArray())));
    }

    [Fact]
    public async Task CancellationStopsAdvertisementBeforeDatabaseWork()
    {
        await using var fixture = await Fixture.CreateAsync();
        var provider = fixture.Provider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.GetCapabilitiesAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await provider.GetCursorsAsync(RemoteHello(), cancellation.Token));
    }

    private static PeerHello RemoteHello() => new()
    {
        PeerId = "peer-remote",
        ProtocolVersion = new ProtocolVersion { Major = 1, Minor = 1 },
        PublicKeySha256 = ByteString.CopyFrom(new byte[32]),
        SessionId = "session-1",
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly EcdsaPeerIdentityKey key;

        private Fixture(string directory, PeerStore store, EcdsaPeerIdentityKey key)
        {
            this.directory = directory;
            this.key = key;
            Store = store;
        }

        public PeerStore Store { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "monitor-advertisement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "peer.db")));
            await store.InitializeAsync();
            var key = EcdsaPeerIdentityKey.Create();
            var identity = PeerPublicIdentity.Create("peer-local", key);
            await store.SetLocalIdentityAsync(new LocalPeerIdentity(
                identity.PeerId,
                identity.PublicKeySpki,
                identity.PublicKeyFingerprint,
                "credential-provider-private",
                "stream-local",
                1,
                Now));
            return new Fixture(directory, store, key);
        }

        public PeerStoreAdvertisementProvider Provider(int maximumCapabilities = 256) => new(
            Store,
            new PeerAdvertisementOptions(maximumCapabilities, 256),
            new FixedTimeProvider(Now));

        public Task AddCapabilityAsync(string id, string @namespace) =>
            Store.UpsertCapabilityAsync(new CapabilityCatalogEntry(
                id,
                @namespace,
                "1.0.0",
                Encoding.UTF8.GetBytes("descriptor-" + id),
                Now));

        public Task<RecordInsertOutcome> AppendLocalAsync(ulong sequence) =>
            Store.AppendOriginRecordAsync(new OriginRecordWrite(
                "peer-local",
                "stream-local",
                sequence,
                "entity-local",
                "adapter-local",
                "signal",
                BitConverter.GetBytes(sequence),
                new byte[64],
                Now.AddSeconds((long)sequence),
                Now.AddSeconds((long)sequence),
                null,
                RecordProvenance.LocalOrigin,
                "Valid"));

        public ValueTask DisposeAsync()
        {
            key.Dispose();
            Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

}
