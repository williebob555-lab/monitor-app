using Xunit;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Model.Tests;

public sealed class SystemModelTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("model-tests-").FullName;
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<ModelStore> OpenAsync(string name = "model.db")
    {
        var store = new ModelStore(Path.Combine(directory, name), timeProvider: clock);
        await store.InitializeAsync();
        return store;
    }

    private static PortSpec Port(string id, string connector, PortDirection direction, params string[] signals) =>
        new(id, id, connector, PortGender.Socket, direction, signals, PortFace.Rear);

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task OneObjectIsSharedByPhysicalConnectionProjectAndDesignViewsAndSurvivesRestart()
    {
        await using (var store = await OpenAsync())
        {
            // Scenario 1 in miniature: an owned amplifier, an enclosure, a speaker, and a project with two alternatives.
            await store.AppendAsync(
            [
                new ObjectCreated("amp", "Class D amplifier", "device.audio.amplifier", Lifecycle.Owned),
                new PortAdded("amp", Port("out-l", "speakon-nl4", PortDirection.Output, "speaker-level")),
                new PropertyAsserted("amp", "mechanical.depth", PropertyValue.Number(210, "mm"), new Evidence(EvidenceLevel.Specified, "datasheet", "amp-manual.pdf")),
                new PropertyAsserted("amp", "thermal.airflow", PropertyValue.Unknown, Evidence.UserEntered),
                new ObjectCreated("cabinet", "Desk cabinet", "space.enclosure", Lifecycle.Owned),
                new PropertyAsserted("cabinet", "mechanical.depth", PropertyValue.Number(20, "cm"), Evidence.UserEntered),
                new ObjectCreated("spk", "Bookshelf speaker", "device.audio.speaker", Lifecycle.Owned),
                new PortAdded("spk", Port("in", "speakon-nl4", PortDirection.Input, "speaker-level")),
                new ObjectCreated("dorm", "Dorm Speaker Setup", "project"),
                new DesignLayerCreated("dorm-a", "dorm", "Amp inside the cabinet"),
                new DesignLayerCreated("dorm-b", "dorm", "Amp on the shelf"),
            ], "test");

            await store.AppendAsync(
            [
                new RelationshipAdded("amp-in-cabinet", RelationshipTypes.Contains, "cabinet", "amp", new Placement(PortFace.Front, new Point3(0, 0, 0), 90)),
                new ConnectionAdded("amp-to-spk", new PortRef("amp", "out-l"), new PortRef("spk", "in")),
                new ObjectCreated("vent", "Extra vent fan", "part.fan", Lifecycle.Planned),
            ], "test", layerId: "dorm-a");

            clock.Advance(TimeSpan.FromHours(1));
            await store.RealizeAsync("vent", "dorm-a", "test", Lifecycle.Ordered);
        }

        await using var reopened = await OpenAsync();
        var actual = await reopened.LoadAsync();
        var designA = await reopened.LoadAsync(layerId: "dorm-a");
        var designB = await reopened.LoadAsync(layerId: "dorm-b");

        // The design never changed the actual world, and the other alternative is untouched.
        Assert.Null(actual.ContainerOf("amp"));
        Assert.Empty(actual.ConnectionsOf("amp"));
        Assert.Null(designB.ContainerOf("amp"));

        // In design A the very same amplifier object sits in the cabinet and drives the speaker.
        Assert.Equal("cabinet", designA.ContainerOf("amp")!.SourceId);
        Assert.Equal(90, designA.ContainerOf("amp")!.Placement!.Rotation);
        Assert.Same(designA.Get("amp"), designA.Get(Assert.Single(designA.ConnectionsOf("spk")).A.ObjectId));
        Assert.Equal(EvidenceLevel.Specified, designA.Get("amp").Strongest("mechanical.depth")!.Evidence.Level);
        Assert.Equal(PropertyValueType.Unknown, designA.Get("amp").Strongest("thermal.airflow")!.Value.Type);

        // The vent fan was proposed in design A, then ordered for real with the same id.
        Assert.Equal(Lifecycle.Ordered, actual.Get("vent").Lifecycle);
        Assert.Null(actual.Get("vent").CreatedInLayer);
        Assert.Null(designA.Get("vent").CreatedInLayer);
        Assert.Empty(designA.Conflicts);

        // Time travel: before the order, the fan did not exist in the actual world.
        var before = await reopened.LoadAsync(asOf: clock.GetUtcNow().AddMinutes(-30));
        Assert.Null(before.Find("vent"));
        Assert.Equal(2, before.Layers.Count);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task InvalidChangesAreRejectedAtomicallyWithAReason()
    {
        await using var store = await OpenAsync();
        await store.AppendAsync(
        [
            new ObjectCreated("rack", "Rack", "assembly.rack", Lifecycle.Owned),
            new ObjectCreated("shelf", "Shelf", "assembly.shelf", Lifecycle.Owned),
            new ObjectCreated("psu", "Pedal power supply", "device.power.supply", Lifecycle.Owned),
            new PortAdded("psu", Port("out1", "dc-5.5x2.1", PortDirection.Output, "dc-9v")),
            new ObjectCreated("pedal", "Fuzz", "device.audio.effect", Lifecycle.Owned),
            new PortAdded("pedal", Port("dc", "dc-5.5x2.1", PortDirection.Input, "dc-9v")),
            new ObjectCreated("pedal2", "Delay", "device.audio.effect", Lifecycle.Owned),
            new PortAdded("pedal2", Port("dc", "dc-5.5x2.1", PortDirection.Input, "dc-9v")),
            new RelationshipAdded("r1", RelationshipTypes.Contains, "rack", "shelf"),
            new ConnectionAdded("c1", new PortRef("psu", "out1"), new PortRef("pedal", "dc")),
        ], "test");

        async Task Rejected(string expected, params ModelChange[] changes)
        {
            var error = await Assert.ThrowsAsync<ModelValidationException>(() => store.AppendAsync(changes, "test"));
            Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        }

        await Rejected("loop", new RelationshipAdded("r2", RelationshipTypes.Contains, "shelf", "rack"));
        await Rejected("already inside", new ObjectCreated("rack2", "Rack 2", "assembly.rack"), new RelationshipAdded("r3", RelationshipTypes.Contains, "rack2", "shelf"));
        await Rejected("already has a physical connection", new ConnectionAdded("c2", new PortRef("psu", "out1"), new PortRef("pedal2", "dc")));
        await Rejected("needs a length unit", new PropertyAsserted("psu", "mechanical.depth", PropertyValue.Number(3, "V"), Evidence.UserEntered));
        await Rejected("back to", new LifecycleMoved("psu", Lifecycle.Candidate));
        await Rejected("is connected", new PortRemoved("pedal", "dc"));

        // The failed batch that created rack2 left nothing behind.
        var model = await store.LoadAsync();
        Assert.Null(model.Find("rack2"));

        // A logical link does not compete with the physical mate.
        await store.AppendAsync(new ConnectionAdded("c3", new PortRef("psu", "out1"), new PortRef("pedal2", "dc"), ConnectionMedium.Logical), "test");

        // Designs can propose but not own, and cannot rewrite a real object's lifecycle.
        await store.AppendAsync([new ObjectCreated("board", "Bass board", "project"), new DesignLayerCreated("d1", "board", "Compact")], "test");
        await Assert.ThrowsAsync<ModelValidationException>(() => store.AppendAsync(new ObjectCreated("iem", "IEM pack", "device.audio.iem", Lifecycle.Owned), "test", "d1"));
        await Assert.ThrowsAsync<ModelValidationException>(() => store.AppendAsync(new LifecycleMoved("psu", Lifecycle.Retired), "test", "d1"));
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task LooseCaptureAndCompetingEvidenceAreKept()
    {
        await using var store = await OpenAsync();
        await store.AppendAsync(
        [
            // Nothing but a name: no kind, no project, an unknown property key and an unknown kind are all accepted.
            new ObjectCreated("idea", "that tiny DI box from the forum?"),
            new PropertyAsserted("idea", "my.weird.key", PropertyValue.Of("ask Sam"), Evidence.UserEntered),
            new ObjectCreated("ups", "UPS", "device.power.custom-thing", Lifecycle.Owned),
            new PropertyAsserted("ups", "electrical.power.max", PropertyValue.Number(45, "W"), new Evidence(EvidenceLevel.Specified, "datasheet")),
            new PropertyAsserted("ups", "electrical.power.max", PropertyValue.Number(52, "W"), new Evidence(EvidenceLevel.Measured, "meter")),
            new PropertyAsserted("ups", "electrical.power.max", PropertyValue.Number(40, "W"), new Evidence(EvidenceLevel.Estimated, "engine")),
            new RelationshipAdded("about", RelationshipTypes.About, "idea", "ups"),
        ], "test");

        var model = await store.LoadAsync();
        Assert.Equal("thing", model.Get("idea").KindId);
        Assert.False(model.Library.IsA("device.power.custom-thing", "device"));
        Assert.True(model.Library.IsA("device.power.ups", "device"));
        var ups = model.Get("ups");
        Assert.Equal(3, ups.AllValues("electrical.power.max").Count());
        Assert.Equal(52, ups.Strongest("electrical.power.max")!.Value.Quantity!.Value.Value);
        Assert.True(Units.TryConvert(new Quantity(20, "cm"), "mm", out var mm));
        Assert.Equal(200, mm.Value, 6);
    }

    [Fact]
    [Trait("Category", "SQLite")]
    public async Task MonitoredPeersAndDevicesBecomeObjectsWithoutNewIdentities()
    {
        var now = clock.GetUtcNow();
        var peers = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "peer.db")));
        await peers.InitializeAsync();
        var keys = new Dictionary<byte, byte[]>();
        byte[] Key(byte seed)
        {
            if (!keys.TryGetValue(seed, out var spki))
            {
                using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
                keys[seed] = spki = ecdsa.ExportSubjectPublicKeyInfo();
            }
            return spki;
        }
        byte[] Print(byte seed) => System.Security.Cryptography.SHA256.HashData(Key(seed));
        await peers.SetLocalIdentityAsync(new LocalPeerIdentity("peer-laptop", Key(1), Print(1), "key-ref", "origin-laptop", 1, now));
        await peers.UpsertTrustedPeerAsync(new TrustedPeer("peer-phone", Key(3), Print(3), "Phone", PeerTrustState.Active, now, null));
        await peers.UpsertTrustedPeerAsync(new TrustedPeer("peer-old", Key(5), Print(5), "Old tablet", PeerTrustState.Revoked, now, now));
        await peers.UpsertObservedEntityAsync(new ObservedEntity("ups-1", "ups", now));
        await peers.AddIdentityClaimAsync(new IdentityClaim("ups-1", "serial-number", "ABC123", IdentityStrength.Strong, "peer-laptop", RelationshipEvidenceKind.Reported, 0.99, now));
        await peers.AddIdentityClaimAsync(new IdentityClaim("ups-1", "ip", "192.168.1.9", IdentityStrength.Weak, "peer-laptop", RelationshipEvidenceKind.Inferred, 0.5, now));

        await using var store = await OpenAsync();
        // Wilson entered the UPS by hand, with its serial number, before monitoring found it.
        await store.AppendAsync(
        [
            new ObjectCreated("my-ups", "Rack UPS", "device.power.ups", Lifecycle.Installed),
            new PropertyAsserted("my-ups", "identity.serial-number", PropertyValue.Of("abc123"), Evidence.UserEntered),
        ], "test");

        var first = await MonitoringBridge.SyncFromPeerStoreAsync(peers, store);
        var second = await MonitoringBridge.SyncFromPeerStoreAsync(peers, store);

        Assert.Equal(new BridgeResult(Created: 2, BoundExisting: 1, AlreadyBound: 0), first);
        Assert.Equal(new BridgeResult(0, 0, 3), second);

        var model = await store.LoadAsync();
        var ups = model.FindBound(MonitoringSubjectKind.ObservedDevice, "ups-1")!;
        Assert.Equal("my-ups", ups.Id);
        Assert.Equal(Lifecycle.Installed, ups.Lifecycle);
        Assert.Equal(EvidenceLevel.Measured, ups.Strongest("identity.serial-number")!.Evidence.Level);
        Assert.Null(ups.Strongest("identity.ip"));
        Assert.Equal("Phone", model.FindBound(MonitoringSubjectKind.Peer, "peer-phone")!.Name);
        Assert.Equal("peer:peer-laptop", model.FindBound(MonitoringSubjectKind.Peer, "peer-laptop")!.Id);
        Assert.Null(model.FindBound(MonitoringSubjectKind.Peer, "peer-old"));
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
