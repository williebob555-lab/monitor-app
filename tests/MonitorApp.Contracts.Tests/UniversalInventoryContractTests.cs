using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;
using Xunit;

namespace MonitorApp.Contracts.Tests;

public sealed class UniversalInventoryContractTests
{
    [Fact]
    [Trait("Category", "Contract")]
    public void RoundTripPreservesSeparateIdentitiesPlanesRelationshipsAndCableMeasurements()
    {
        var snapshot = ValidSnapshot();

        var parsed = UniversalInventorySnapshot.Parser.ParseFrom(snapshot.ToByteArray());
        var result = UniversalInventoryContractValidator.Validate(parsed);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(
            UniversalObjectEnvelope.IdentityOneofCase.Product,
            parsed.Objects.Single(item => item.ObjectId == "object-product-usb-c").IdentityCase);
        Assert.Equal(
            UniversalObjectEnvelope.IdentityOneofCase.Offer,
            parsed.Objects.Single(item => item.ObjectId == "object-offer-usb-c").IdentityCase);
        Assert.Equal(
            UniversalObjectEnvelope.IdentityOneofCase.Instance,
            parsed.Objects.Single(item => item.ObjectId == "object-instance-cable-1").IdentityCase);

        var planned = parsed.Objects.Single(item => item.ObjectId == "object-plan-cable-1");
        Assert.Equal(StatePlane.Design, planned.Plane);
        Assert.Equal("scenario-studio-wiring", planned.DesignScope.ScenarioId);

        var assignment = parsed.Relationships.Single(item =>
            item.RelationshipTypeId == "monitor.relationship.assigned_to");
        Assert.Equal(StatePlane.Design, assignment.Plane);
        Assert.Equal("object-instance-cable-1", assignment.TargetObjectId);

        var cable = parsed.Cables.Single(item => item.ObjectId == "object-instance-cable-1");
        Assert.Equal("connector.usb.c", cable.EndA.ConnectorStandardId);
        Assert.Equal(2, cable.NominalLength.Value);
        Assert.Equal("unit.meter", cable.NominalLength.Unit);
        Assert.Equal(1.98, cable.MeasuredLength.Value);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActualRelationshipCannotReferenceDesignOnlyObject()
    {
        var snapshot = ValidSnapshot();
        var relationship = snapshot.Relationships.Single(item =>
            item.RelationshipTypeId == "monitor.relationship.assigned_to");
        relationship.Plane = StatePlane.Actual;
        relationship.DesignScope = null;

        var result = UniversalInventoryContractValidator.Validate(snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("silently promote", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DesignObjectsAndRelationshipsMustShareNamedScenarioRevision()
    {
        var snapshot = ValidSnapshot();
        var relationship = snapshot.Relationships.Single(item =>
            item.RelationshipTypeId == "monitor.relationship.assigned_to");
        relationship.DesignScope.RevisionId = "revision-other";

        var result = UniversalInventoryContractValidator.Validate(snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("same project, scenario, and revision", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProductOfferAndPhysicalInstanceReferencesCannotCollapseIntoOneIdentity()
    {
        var snapshot = ValidSnapshot();
        var offer = snapshot.Objects.Single(item => item.ObjectId == "object-offer-usb-c");
        offer.Offer.ProductObjectId = "object-instance-cable-1";

        var result = UniversalInventoryContractValidator.Validate(snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("expected distinct object identity", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CableSliceRequiresTwoStructuredEndsDirectionAndUnitBearingPositiveLength()
    {
        var snapshot = ValidSnapshot();
        var cable = snapshot.Cables.Single(item => item.ObjectId == "object-instance-cable-1");
        cable.EndA.Role = ConnectorRole.Source;
        cable.NominalLength.Value = 0;
        cable.NominalLength.Unit = string.Empty;
        cable.MeasuredLength = null;

        var result = UniversalInventoryContractValidator.Validate(snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("bidirectional connector ends", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("finite and positive", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("length unit is required", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("require a measured cable length", StringComparison.Ordinal));
    }

    private static UniversalInventorySnapshot ValidSnapshot()
    {
        var scope = new DesignScope
        {
            ProjectId = "project-studio",
            ScenarioId = "scenario-studio-wiring",
            RevisionId = "revision-1",
        };
        var now = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var provenance = Provenance();

        return new UniversalInventorySnapshot
        {
            Objects =
            {
                new UniversalObjectEnvelope
                {
                    ObjectId = "object-product-usb-c",
                    ObjectTypeId = "monitor.object.cable",
                    DisplayName = "Example USB-C cable product",
                    Plane = StatePlane.Actual,
                    Provenance = provenance.Clone(),
                    ValidFrom = now.Clone(),
                    Product = new ProductIdentity
                    {
                        Manufacturer = "Example Manufacturer",
                        ModelNumber = "CABLE-2M",
                    },
                },
                new UniversalObjectEnvelope
                {
                    ObjectId = "object-offer-usb-c",
                    ObjectTypeId = "monitor.object.seller_offer",
                    DisplayName = "Example seller offer",
                    Plane = StatePlane.Actual,
                    Provenance = provenance.Clone(),
                    ValidFrom = now.Clone(),
                    Offer = new SellerOfferIdentity
                    {
                        SellerId = "seller-example",
                        SellerOfferKey = "offer-123",
                        ProductObjectId = "object-product-usb-c",
                    },
                },
                new UniversalObjectEnvelope
                {
                    ObjectId = "object-instance-cable-1",
                    ObjectTypeId = "monitor.object.cable",
                    DisplayName = "Owned USB-C cable 1",
                    Plane = StatePlane.Actual,
                    Provenance = provenance.Clone(),
                    ValidFrom = now.Clone(),
                    Instance = new PhysicalInstanceIdentity
                    {
                        InstanceKey = "cable-1",
                        SerialNumber = "SERIAL-1",
                        ProductObjectId = "object-product-usb-c",
                        AcquiredFromOfferObjectId = "object-offer-usb-c",
                    },
                },
                new UniversalObjectEnvelope
                {
                    ObjectId = "object-plan-cable-1",
                    ObjectTypeId = "monitor.object.cable",
                    DisplayName = "Planned desk cable",
                    Plane = StatePlane.Design,
                    DesignScope = scope.Clone(),
                    Provenance = provenance.Clone(),
                    ValidFrom = now.Clone(),
                    DesignPlaceholder = new DesignPlaceholderIdentity
                    {
                        PlaceholderKey = "desk-cable",
                    },
                },
            },
            Relationships =
            {
                new TypedRelationship
                {
                    RelationshipId = "relationship-instance-product",
                    RelationshipTypeId = "monitor.relationship.instance_of",
                    SourceObjectId = "object-instance-cable-1",
                    TargetObjectId = "object-product-usb-c",
                    Direction = RelationshipDirection.Directed,
                    Plane = StatePlane.Actual,
                    Provenance = provenance.Clone(),
                    ValidFrom = now.Clone(),
                },
                new TypedRelationship
                {
                    RelationshipId = "relationship-plan-assignment",
                    RelationshipTypeId = "monitor.relationship.assigned_to",
                    SourceObjectId = "object-plan-cable-1",
                    TargetObjectId = "object-instance-cable-1",
                    Direction = RelationshipDirection.Directed,
                    Plane = StatePlane.Design,
                    DesignScope = scope.Clone(),
                    Provenance = provenance.Clone(),
                    ValidFrom = now.Clone(),
                },
            },
            Cables =
            {
                new CableSpecification
                {
                    ObjectId = "object-instance-cable-1",
                    EndA = BidirectionalUsbC(),
                    EndB = BidirectionalUsbC(),
                    Directionality = CableDirectionality.Reversible,
                    NominalLength = new UnitLength { Value = 2, Unit = "unit.meter" },
                    MeasuredLength = new UnitLength { Value = 1.98, Unit = "unit.meter" },
                    MeasuredAt = now.Clone(),
                    MeasurementMethod = "manual.tape_measure",
                },
                new CableSpecification
                {
                    ObjectId = "object-plan-cable-1",
                    EndA = BidirectionalUsbC(),
                    EndB = BidirectionalUsbC(),
                    Directionality = CableDirectionality.Reversible,
                    NominalLength = new UnitLength { Value = 3, Unit = "unit.meter" },
                },
            },
        };
    }

    private static ConnectorEnd BidirectionalUsbC() => new()
    {
        ConnectorStandardId = "connector.usb.c",
        Form = ConnectorForm.Plug,
        Role = ConnectorRole.Bidirectional,
    };

    private static AssertionProvenance Provenance() => new()
    {
        Source = "monitor.source.user",
        EvidenceKind = EvidenceKind.UserAsserted,
        Quality = ObservationQuality.Valid,
        HasConfidence = true,
        Confidence = 1,
    };
}
