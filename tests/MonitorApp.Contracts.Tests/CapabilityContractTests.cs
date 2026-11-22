using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using Xunit;

namespace MonitorApp.Contracts.Tests;

public sealed class CapabilityContractTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void ModuleRoundTripPreservesCanonicalCatalogAndFallbackMetadata()
    {
        var declaration = OrdinaryDeclaration();

        var parsed = CapabilityModuleDeclaration.Parser.ParseFrom(declaration.ToByteArray());
        var result = CapabilityContractValidator.Validate(parsed);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("monitor.system", parsed.ModuleId);
        var observation = Assert.Single(parsed.Observations);
        Assert.Equal("monitor.system.memory.used", observation.SignalId);
        Assert.Equal("unit.bytes", observation.Unit);
        Assert.Equal(ObservationPrivacyClass.Operational, observation.Privacy);
        Assert.Equal(ObservationCollectionCost.Low, observation.CollectionCost);
        Assert.Equal(NetworkDataClass.NotNetworkData, observation.NetworkDataClass);
        Assert.Equal(InspectorKind.GeneratedFallback, Assert.Single(parsed.Inspectors).Kind);
    }

    [Theory]
    [InlineData(NetworkDataClass.CommunicationIdentity)]
    [InlineData(NetworkDataClass.FlowOrSession)]
    [InlineData(NetworkDataClass.DnsOrQueryHistory)]
    [InlineData(NetworkDataClass.PacketDetail)]
    [InlineData(NetworkDataClass.PerApplicationOrProcess)]
    [InlineData(NetworkDataClass.Content)]
    [Trait("Category", "Unit")]
    public void OrdinaryMonitoringRejectsProhibitedCommunicationData(NetworkDataClass dataClass)
    {
        var declaration = OrdinaryDeclaration();
        declaration.Observations[0].NetworkDataClass = dataClass;

        var result = CapabilityContractValidator.Validate(declaration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Ordinary monitoring", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(NetworkDataClass.AggregateOperational)]
    [InlineData(NetworkDataClass.InfrastructureFact)]
    [Trait("Category", "Unit")]
    public void OrdinaryMonitoringAllowsDeclaredAggregateAndInfrastructureData(NetworkDataClass dataClass)
    {
        var declaration = OrdinaryDeclaration();
        declaration.Observations[0].NetworkDataClass = dataClass;

        var result = CapabilityContractValidator.Validate(declaration);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DefensiveSecurityRequiresSeparateEnablementEvidenceConfidenceAndBoundedRetention()
    {
        var declaration = OrdinaryDeclaration();
        declaration.ModuleKind = CapabilityModuleKind.DefensiveSecurity;
        var observation = declaration.Observations[0];
        observation.NetworkDataClass = NetworkDataClass.FlowOrSession;
        observation.Privacy = ObservationPrivacyClass.DefensiveSecurity;

        var rejected = CapabilityContractValidator.Validate(declaration);

        Assert.False(rejected.IsValid);
        Assert.Contains(rejected.Errors, error => error.Contains("separate enablement", StringComparison.Ordinal));

        observation.RequiresExplicitEnablement = true;
        observation.RequiresEvidence = true;
        observation.RequiresConfidence = true;
        var accepted = CapabilityContractValidator.Validate(declaration);

        Assert.True(accepted.IsValid, string.Join(Environment.NewLine, accepted.Errors));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DeclarationRejectsIncompatibleHostMissingFallbackUnknownReferencesAndMissingUnit()
    {
        var declaration = OrdinaryDeclaration();
        declaration.CompatiblePeerProtocol.MinimumMajor = 2;
        declaration.CompatiblePeerProtocol.MaximumMajor = 2;
        declaration.Observations[0].EntityTypeId = "monitor.entity.missing";
        declaration.Observations[0].Unit = string.Empty;
        declaration.Inspectors[0].Kind = InspectorKind.PurposeBuilt;
        declaration.Inspectors[0].SignalIds[0] = "monitor.system.missing";

        var result = CapabilityContractValidator.Validate(declaration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("current peer protocol", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("same declaration", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("explicit unit", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("exactly one", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Contract")]
    public void ModuleDeclarationSchemaCannotCarryCredentialsOrPrivateMaterial()
    {
        var fieldNames = CapabilityModuleDeclaration.Descriptor.Fields
            .InFieldNumberOrder()
            .Concat(ObservationDeclaration.Descriptor.Fields.InFieldNumberOrder())
            .Select(field => field.Name)
            .ToArray();

        Assert.DoesNotContain(fieldNames, field =>
            field.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("private_key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InstalledPurposeBuiltInspectorIsSelectedForItsDeclaredEntity()
    {
        var declaration = OrdinaryDeclaration();
        declaration.Inspectors.Add(new InspectorDescriptor
        {
            InspectorId = "monitor.inspector.system_detail",
            Title = "System detail",
            Kind = InspectorKind.PurposeBuilt,
            SignalIds = { "monitor.system.memory.used" },
        });

        var selection = CapabilityInspectorSelector.Select(
            declaration,
            "monitor.entity.peer",
            new HashSet<string>(["monitor.inspector.system_detail"], StringComparer.Ordinal));

        Assert.False(selection.IsGeneratedFallback);
        Assert.Equal("monitor.inspector.system_detail", selection.Descriptor.InspectorId);

        selection.Descriptor.Title = "mutated caller copy";
        Assert.Equal("System detail", declaration.Inspectors[1].Title);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MissingPurposeBuiltImplementationTruthfullySelectsGeneratedFallback()
    {
        var declaration = OrdinaryDeclaration();
        declaration.Inspectors.Add(new InspectorDescriptor
        {
            InspectorId = "monitor.inspector.system_detail",
            Title = "System detail",
            Kind = InspectorKind.PurposeBuilt,
            SignalIds = { "monitor.system.memory.used" },
        });

        var selection = CapabilityInspectorSelector.Select(
            declaration,
            "monitor.entity.peer",
            new HashSet<string>(StringComparer.Ordinal));

        Assert.True(selection.IsGeneratedFallback);
        Assert.Equal("monitor.inspector.system_fallback", selection.Descriptor.InspectorId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InspectorSelectionRejectsUnknownEntityAndInvalidDeclaration()
    {
        var declaration = OrdinaryDeclaration();

        Assert.Throws<ArgumentException>(() => CapabilityInspectorSelector.Select(
            declaration,
            "monitor.entity.unknown",
            new HashSet<string>(StringComparer.Ordinal)));

        declaration.Inspectors.Clear();
        Assert.Throws<ArgumentException>(() => CapabilityInspectorSelector.Select(
            declaration,
            "monitor.entity.peer",
            new HashSet<string>(StringComparer.Ordinal)));
    }

    private static CapabilityModuleDeclaration OrdinaryDeclaration() => new()
    {
        ModuleId = "monitor.system",
        ModuleVersion = new SemanticVersion { Major = 1 },
        CompatiblePeerProtocol = new ProtocolCompatibility
        {
            MinimumMajor = PeerContractRules.ProtocolMajor,
            MinimumMinor = PeerContractRules.BilateralSessionProofMinor,
            MaximumMajor = PeerContractRules.ProtocolMajor,
            MaximumMinor = PeerContractRules.BilateralSessionProofMinor,
        },
        ModuleKind = CapabilityModuleKind.OrdinaryMonitoring,
        EntityTypes =
        {
            new EntityTypeDeclaration
            {
                EntityTypeId = "monitor.entity.peer",
                DisplayName = "Monitor App peer",
            },
        },
        Observations =
        {
            new ObservationDeclaration
            {
                SignalId = "monitor.system.memory.used",
                EntityTypeId = "monitor.entity.peer",
                Domain = "monitor.domain.system",
                Subsystem = "monitor.subsystem.memory",
                Source = "monitor.source.operating_system",
                ValueKind = ObservationValueKind.Numeric,
                Unit = "unit.bytes",
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
                Title = "System observations",
                Kind = InspectorKind.GeneratedFallback,
                SignalIds = { "monitor.system.memory.used" },
            },
        },
    };
}
