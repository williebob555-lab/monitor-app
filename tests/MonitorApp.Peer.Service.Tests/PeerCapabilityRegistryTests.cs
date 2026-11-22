using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Persistence;
using MonitorApp.Peer.Service;
using Xunit;

namespace MonitorApp.Peer.Service.Tests;

public sealed class PeerCapabilityRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidDeclarationPersistsAndAdvertisesItsExactVersionAndHash()
    {
        await using var fixture = await Fixture.CreateAsync();
        var declaration = Declaration();

        var registered = await fixture.Registry.RegisterAsync(declaration);
        var restored = await fixture.Registry.ReadAsync(declaration.ModuleId);
        var advertised = Assert.Single(await fixture.Advertisement.GetCapabilitiesAsync(CancellationToken.None));

        Assert.Equal("monitor.system", registered.ModuleId);
        Assert.Equal("1.2.3", registered.ModuleVersion);
        Assert.Equal(registered, advertised);
        Assert.Equal(declaration, restored);
        Assert.Equal(Now, Assert.Single(await fixture.Store.ReadCapabilitiesAsync()).UpdatedAt);
    }

    [Fact]
    public async Task ProhibitedOrdinaryNetworkDeclarationFailsBeforePersistence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var declaration = Declaration();
        declaration.Observations[0].NetworkDataClass = NetworkDataClass.CommunicationIdentity;

        var exception = await Assert.ThrowsAsync<CapabilityRegistrationException>(() =>
            fixture.Registry.RegisterAsync(declaration));

        Assert.Contains(exception.Errors, error => error.Contains("Ordinary monitoring", StringComparison.Ordinal));
        Assert.Empty(await fixture.Store.ReadCapabilitiesAsync());
    }

    [Fact]
    public async Task SeparatelyEnabledDefensiveDeclarationMayRegisterDeeperEvidenceClass()
    {
        await using var fixture = await Fixture.CreateAsync();
        var declaration = Declaration();
        declaration.ModuleKind = CapabilityModuleKind.DefensiveSecurity;
        var observation = declaration.Observations[0];
        observation.Privacy = ObservationPrivacyClass.DefensiveSecurity;
        observation.NetworkDataClass = NetworkDataClass.FlowOrSession;
        observation.RequiresExplicitEnablement = true;
        observation.RequiresEvidence = true;
        observation.RequiresConfidence = true;

        var registered = await fixture.Registry.RegisterAsync(declaration);

        Assert.Equal(declaration.ModuleId, registered.ModuleId);
        Assert.NotNull(await fixture.Registry.ReadAsync(declaration.ModuleId));
    }

    private static CapabilityModuleDeclaration Declaration() => new()
    {
        ModuleId = "monitor.system",
        ModuleVersion = new SemanticVersion { Major = 1, Minor = 2, Patch = 3 },
        CompatiblePeerProtocol = new ProtocolCompatibility
        {
            MinimumMajor = 1,
            MinimumMinor = 1,
            MaximumMajor = 1,
            MaximumMinor = 1,
        },
        ModuleKind = CapabilityModuleKind.OrdinaryMonitoring,
        EntityTypes =
        {
            new EntityTypeDeclaration
            {
                EntityTypeId = "monitor.entity.peer",
                DisplayName = "Peer",
            },
        },
        Observations =
        {
            new ObservationDeclaration
            {
                SignalId = "monitor.system.cpu.utilization",
                EntityTypeId = "monitor.entity.peer",
                Domain = "monitor.domain.system",
                Subsystem = "monitor.subsystem.cpu",
                Source = "monitor.source.operating_system",
                ValueKind = ObservationValueKind.Numeric,
                Unit = "unit.ratio",
                TemporalBehavior = ObservationTemporalBehavior.Gauge,
                Importance = ObservationImportance.Normal,
                Privacy = ObservationPrivacyClass.Operational,
                CollectionCost = ObservationCollectionCost.Low,
                NetworkDataClass = NetworkDataClass.NotNetworkData,
                DefaultSamplingInterval = Duration.FromTimeSpan(TimeSpan.FromSeconds(30)),
                StaleAfter = Duration.FromTimeSpan(TimeSpan.FromMinutes(2)),
                DefaultRetention = Duration.FromTimeSpan(TimeSpan.FromDays(30)),
            },
        },
        Inspectors =
        {
            new InspectorDescriptor
            {
                InspectorId = "monitor.inspector.system_fallback",
                Title = "System",
                Kind = InspectorKind.GeneratedFallback,
                SignalIds = { "monitor.system.cpu.utilization" },
            },
        },
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;

        private Fixture(string directory, PeerStore store)
        {
            this.directory = directory;
            Store = store;
            Registry = new PeerCapabilityRegistry(store, new FixedTimeProvider(Now));
            Advertisement = new PeerStoreAdvertisementProvider(
                store,
                PeerAdvertisementOptions.Default,
                new FixedTimeProvider(Now));
        }

        public PeerStore Store { get; }

        public PeerCapabilityRegistry Registry { get; }

        public PeerStoreAdvertisementProvider Advertisement { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "monitor-capabilities-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(directory, "peer.db")));
            await store.InitializeAsync();
            return new Fixture(directory, store);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
