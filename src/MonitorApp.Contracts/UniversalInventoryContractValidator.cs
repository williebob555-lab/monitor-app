using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Contracts;

public static class UniversalInventoryContractValidator
{
    public const int MaximumObjects = 4096;
    public const int MaximumRelationships = 16384;
    public const int MaximumCables = 4096;

    public static ContractValidationResult Validate(UniversalInventorySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return ContractValidationResult.Failure(["Universal inventory snapshot is required."]);
        }

        var errors = new List<string>();
        RequireBound(snapshot.Objects.Count, 1, MaximumObjects, "Objects", errors);
        RequireBound(snapshot.Relationships.Count, 0, MaximumRelationships, "Relationships", errors);
        RequireBound(snapshot.Cables.Count, 0, MaximumCables, "Cable specifications", errors);

        var objects = UniqueById(snapshot.Objects, item => item.ObjectId, "Object id", errors);
        foreach (var item in snapshot.Objects)
        {
            ValidateObject(item, errors);
        }

        ValidateIdentityReferences(snapshot.Objects, objects, errors);

        _ = UniqueById(
            snapshot.Relationships,
            item => item.RelationshipId,
            "Relationship id",
            errors);
        foreach (var relationship in snapshot.Relationships)
        {
            ValidateRelationship(relationship, objects, errors);
        }

        var cableObjectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cable in snapshot.Cables)
        {
            if (!cableObjectIds.Add(cable.ObjectId))
            {
                errors.Add("Each cable object can have only one cable specification.");
            }

            ValidateCable(cable, objects, errors);
        }

        return errors.Count == 0
            ? ContractValidationResult.Success()
            : ContractValidationResult.Failure(errors);
    }

    private static void ValidateObject(UniversalObjectEnvelope item, List<string> errors)
    {
        RequireId(item.ObjectId, "Object id", errors);
        RequireNamespacedId(item.ObjectTypeId, "Object type id", errors);
        RequireText(item.DisplayName, "Object display name", errors);
        RequireEnum(item.Plane, "Object state plane", errors);
        ValidateScope(item.Plane, item.DesignScope, "Object", errors);
        ValidateProvenance(item.Provenance, "Object provenance", errors);
        ValidateInterval(item.ValidFrom, item.ValidTo, "Object validity", errors);

        switch (item.IdentityCase)
        {
            case UniversalObjectEnvelope.IdentityOneofCase.Product:
                RequireActual(item.Plane, "Product definitions", errors);
                RequireText(item.Product.Manufacturer, "Product manufacturer", errors);
                RequireText(item.Product.ModelNumber, "Product model number", errors);
                break;
            case UniversalObjectEnvelope.IdentityOneofCase.Offer:
                RequireActual(item.Plane, "Seller offers", errors);
                RequireText(item.Offer.SellerId, "Offer seller id", errors);
                RequireText(item.Offer.SellerOfferKey, "Seller offer key", errors);
                RequireId(item.Offer.ProductObjectId, "Offer product object id", errors);
                break;
            case UniversalObjectEnvelope.IdentityOneofCase.Instance:
                RequireActual(item.Plane, "Physical instances", errors);
                RequireText(item.Instance.InstanceKey, "Physical instance key", errors);
                OptionalId(item.Instance.ProductObjectId, "Instance product object id", errors);
                OptionalId(item.Instance.AcquiredFromOfferObjectId, "Instance offer object id", errors);
                break;
            case UniversalObjectEnvelope.IdentityOneofCase.DesignPlaceholder:
                if (item.Plane != StatePlane.Design)
                {
                    errors.Add("Design placeholders must remain in the design plane.");
                }

                RequireText(item.DesignPlaceholder.PlaceholderKey, "Design placeholder key", errors);
                break;
            case UniversalObjectEnvelope.IdentityOneofCase.GenericActual:
                RequireActual(item.Plane, "Generic actual identities", errors);
                RequireNamespacedId(item.GenericActual.Authority, "Generic identity authority", errors);
                RequireText(item.GenericActual.StableKey, "Generic stable key", errors);
                break;
            default:
                errors.Add("Every object requires an explicit product, offer, instance, design-placeholder, or generic-actual identity.");
                break;
        }

        if (item.Tags.Count > 64 || item.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 128))
        {
            errors.Add("Object tags must contain at most 64 non-empty values of 128 characters or fewer.");
        }
    }

    private static void ValidateIdentityReferences(
        IEnumerable<UniversalObjectEnvelope> items,
        Dictionary<string, UniversalObjectEnvelope> objects,
        List<string> errors)
    {
        foreach (var item in items)
        {
            if (item.IdentityCase == UniversalObjectEnvelope.IdentityOneofCase.Offer)
            {
                RequireIdentityReference(
                    item.Offer.ProductObjectId,
                    UniversalObjectEnvelope.IdentityOneofCase.Product,
                    "Offer product reference",
                    objects,
                    errors);
            }
            else if (item.IdentityCase == UniversalObjectEnvelope.IdentityOneofCase.Instance)
            {
                if (!string.IsNullOrWhiteSpace(item.Instance.ProductObjectId))
                {
                    RequireIdentityReference(
                        item.Instance.ProductObjectId,
                        UniversalObjectEnvelope.IdentityOneofCase.Product,
                        "Instance product reference",
                        objects,
                        errors);
                }

                if (!string.IsNullOrWhiteSpace(item.Instance.AcquiredFromOfferObjectId))
                {
                    RequireIdentityReference(
                        item.Instance.AcquiredFromOfferObjectId,
                        UniversalObjectEnvelope.IdentityOneofCase.Offer,
                        "Instance offer reference",
                        objects,
                        errors);

                    if (objects.TryGetValue(item.Instance.AcquiredFromOfferObjectId, out var offer) &&
                        !string.IsNullOrWhiteSpace(item.Instance.ProductObjectId) &&
                        !string.Equals(
                            offer.Offer.ProductObjectId,
                            item.Instance.ProductObjectId,
                            StringComparison.Ordinal))
                    {
                        errors.Add("An instance's product and seller-offer references must agree.");
                    }
                }
            }
        }
    }

    private static void ValidateRelationship(
        TypedRelationship relationship,
        Dictionary<string, UniversalObjectEnvelope> objects,
        List<string> errors)
    {
        RequireId(relationship.RelationshipId, "Relationship id", errors);
        RequireNamespacedId(relationship.RelationshipTypeId, "Relationship type id", errors);
        RequireId(relationship.SourceObjectId, "Relationship source object id", errors);
        RequireId(relationship.TargetObjectId, "Relationship target object id", errors);
        if (string.Equals(relationship.SourceObjectId, relationship.TargetObjectId, StringComparison.Ordinal))
        {
            errors.Add("A relationship cannot connect an object to itself.");
        }

        RequireEnum(relationship.Direction, "Relationship direction", errors);
        RequireEnum(relationship.Plane, "Relationship state plane", errors);
        ValidateScope(relationship.Plane, relationship.DesignScope, "Relationship", errors);
        ValidateProvenance(relationship.Provenance, "Relationship provenance", errors);
        ValidateInterval(relationship.ValidFrom, relationship.ValidTo, "Relationship validity", errors);

        objects.TryGetValue(relationship.SourceObjectId, out var source);
        objects.TryGetValue(relationship.TargetObjectId, out var target);
        if (source is null)
        {
            errors.Add("Relationship source must reference an object in the same snapshot.");
        }

        if (target is null)
        {
            errors.Add("Relationship target must reference an object in the same snapshot.");
        }

        if (relationship.Plane == StatePlane.Actual &&
            (source?.Plane == StatePlane.Design || target?.Plane == StatePlane.Design))
        {
            errors.Add("Actual relationships cannot silently promote or reference design-only objects.");
        }

        if (relationship.Plane == StatePlane.Design)
        {
            ValidateDesignEndpointScope(relationship.DesignScope, source, errors);
            ValidateDesignEndpointScope(relationship.DesignScope, target, errors);
        }
    }

    private static void ValidateCable(
        CableSpecification cable,
        Dictionary<string, UniversalObjectEnvelope> objects,
        List<string> errors)
    {
        RequireId(cable.ObjectId, "Cable object id", errors);
        if (!objects.TryGetValue(cable.ObjectId, out var item))
        {
            errors.Add("Cable specification must reference an object in the same snapshot.");
        }
        else
        {
            if (!string.Equals(item.ObjectTypeId, "monitor.object.cable", StringComparison.Ordinal))
            {
                errors.Add("Cable specifications require the monitor.object.cable object type.");
            }

            if (item.IdentityCase is
                UniversalObjectEnvelope.IdentityOneofCase.Offer or
                UniversalObjectEnvelope.IdentityOneofCase.GenericActual)
            {
                errors.Add("Cable specifications attach to product definitions, physical instances, or design placeholders, not offers or generic objects.");
            }
        }

        ValidateConnector(cable.EndA, "Cable end A", errors);
        ValidateConnector(cable.EndB, "Cable end B", errors);
        RequireEnum(cable.Directionality, "Cable directionality", errors);
        ValidateLength(cable.NominalLength, "Nominal cable length", required: true, errors);
        ValidateLength(cable.MeasuredLength, "Measured cable length", required: false, errors);

        if (cable.Directionality == CableDirectionality.Reversible &&
            (cable.EndA?.Role != ConnectorRole.Bidirectional ||
             cable.EndB?.Role != ConnectorRole.Bidirectional))
        {
            errors.Add("Reversible cables require two bidirectional connector ends.");
        }

        if (cable.Directionality is CableDirectionality.Directional or CableDirectionality.Conversion &&
            (cable.EndA?.Role != ConnectorRole.Source ||
             cable.EndB?.Role != ConnectorRole.Destination))
        {
            errors.Add("Directional and conversion cables require source end A and destination end B.");
        }

        if (cable.MeasuredLength is null)
        {
            if (cable.MeasuredAt is not null || !string.IsNullOrWhiteSpace(cable.MeasurementMethod))
            {
                errors.Add("Measurement time and method require a measured cable length.");
            }
        }
        else
        {
            RequireTimestamp(cable.MeasuredAt, "Cable measurement time", errors);
            RequireText(cable.MeasurementMethod, "Cable measurement method", errors);
        }
    }

    private static void ValidateConnector(ConnectorEnd? connector, string fieldName, List<string> errors)
    {
        if (connector is null)
        {
            errors.Add($"{fieldName} is required.");
            return;
        }

        RequireNamespacedId(connector.ConnectorStandardId, $"{fieldName} standard id", errors);
        RequireEnum(connector.Form, $"{fieldName} form", errors);
        RequireEnum(connector.Role, $"{fieldName} role", errors);
    }

    private static void ValidateLength(
        UnitLength? length,
        string fieldName,
        bool required,
        List<string> errors)
    {
        if (length is null)
        {
            if (required)
            {
                errors.Add($"{fieldName} is required.");
            }

            return;
        }

        if (!double.IsFinite(length.Value) || length.Value <= 0)
        {
            errors.Add($"{fieldName} must be finite and positive.");
        }

        RequireNamespacedId(length.Unit, $"{fieldName} unit", errors);
    }

    private static void ValidateScope(
        StatePlane plane,
        DesignScope? scope,
        string fieldName,
        List<string> errors)
    {
        if (plane == StatePlane.Actual)
        {
            if (scope is not null)
            {
                errors.Add($"{fieldName} in the actual plane cannot carry a design scope.");
            }

            return;
        }

        if (plane != StatePlane.Design)
        {
            return;
        }

        if (scope is null)
        {
            errors.Add($"{fieldName} in the design plane requires a named project, scenario, and revision.");
            return;
        }

        RequireId(scope.ProjectId, "Design project id", errors);
        RequireId(scope.ScenarioId, "Design scenario id", errors);
        RequireId(scope.RevisionId, "Design revision id", errors);
    }

    private static void ValidateProvenance(
        AssertionProvenance? provenance,
        string fieldName,
        List<string> errors)
    {
        if (provenance is null)
        {
            errors.Add($"{fieldName} is required.");
            return;
        }

        RequireNamespacedId(provenance.Source, $"{fieldName} source", errors);
        RequireEnum(provenance.EvidenceKind, $"{fieldName} evidence kind", errors);
        RequireEnum(provenance.Quality, $"{fieldName} quality", errors);
        if (!provenance.HasConfidence ||
            !double.IsFinite(provenance.Confidence) ||
            provenance.Confidence is < 0 or > 1)
        {
            errors.Add($"{fieldName} requires an explicit confidence from zero through one.");
        }

        if (provenance.EvidenceReferenceIds.Count > 64 ||
            provenance.EvidenceReferenceIds.Any(reference =>
                string.IsNullOrWhiteSpace(reference) || reference.Length > 256))
        {
            errors.Add($"{fieldName} evidence references must contain at most 64 non-empty bounded values.");
        }
    }

    private static void ValidateInterval(
        Timestamp? validFrom,
        Timestamp? validTo,
        string fieldName,
        List<string> errors)
    {
        var start = RequireTimestamp(validFrom, $"{fieldName} start", errors);
        var end = OptionalTimestamp(validTo, $"{fieldName} end", errors);
        if (start.HasValue && end.HasValue && end <= start)
        {
            errors.Add($"{fieldName} end must be later than its start.");
        }
    }

    private static void ValidateDesignEndpointScope(
        DesignScope? relationshipScope,
        UniversalObjectEnvelope? endpoint,
        List<string> errors)
    {
        if (endpoint?.Plane != StatePlane.Design || relationshipScope is null)
        {
            return;
        }

        if (endpoint.DesignScope is null ||
            !string.Equals(endpoint.DesignScope.ProjectId, relationshipScope.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(endpoint.DesignScope.ScenarioId, relationshipScope.ScenarioId, StringComparison.Ordinal) ||
            !string.Equals(endpoint.DesignScope.RevisionId, relationshipScope.RevisionId, StringComparison.Ordinal))
        {
            errors.Add("Design relationships and design-only endpoints must share the same project, scenario, and revision.");
        }
    }

    private static void RequireIdentityReference(
        string objectId,
        UniversalObjectEnvelope.IdentityOneofCase expectedIdentity,
        string fieldName,
        Dictionary<string, UniversalObjectEnvelope> objects,
        List<string> errors)
    {
        if (!objects.TryGetValue(objectId, out var target) || target.IdentityCase != expectedIdentity)
        {
            errors.Add($"{fieldName} must reference the expected distinct object identity in the same snapshot.");
        }
    }

    private static Dictionary<string, T> UniqueById<T>(
        IEnumerable<T> values,
        Func<T, string> selectId,
        string fieldName,
        List<string> errors)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var id = selectId(value);
            if (!result.TryAdd(id, value))
            {
                errors.Add($"{fieldName} values must be unique.");
            }
        }

        return result;
    }

    private static void RequireActual(StatePlane plane, string subject, List<string> errors)
    {
        if (plane != StatePlane.Actual)
        {
            errors.Add($"{subject} are externally real identities and must remain in the actual plane.");
        }
    }

    private static void RequireId(string? value, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            errors.Add($"{fieldName} is required and must not exceed 256 characters.");
        }
    }

    private static void OptionalId(string? value, string fieldName, List<string> errors)
    {
        if (!string.IsNullOrEmpty(value))
        {
            RequireId(value, fieldName, errors);
        }
    }

    private static void RequireNamespacedId(string? value, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            errors.Add($"{fieldName} is required and must not exceed 256 characters.");
            return;
        }

        var segments = value.Split('.');
        if (segments.Length < 2 || segments.Any(segment =>
                segment.Length == 0 ||
                segment[0] is < 'a' or > 'z' ||
                segment.Any(character =>
                    character is not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and
                    not '-' and
                    not '_')))
        {
            errors.Add($"{fieldName} must be a lowercase namespaced identifier.");
        }
    }

    private static void RequireText(string? value, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            errors.Add($"{fieldName} is required and must not exceed 256 characters.");
        }
    }

    private static void RequireEnum<T>(T value, string fieldName, List<string> errors)
        where T : struct, System.Enum
    {
        if (Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 0 ||
            !System.Enum.IsDefined(value))
        {
            errors.Add($"{fieldName} is required and must be recognized.");
        }
    }

    private static DateTimeOffset? RequireTimestamp(
        Timestamp? value,
        string fieldName,
        List<string> errors)
    {
        if (value is null)
        {
            errors.Add($"{fieldName} is required.");
            return null;
        }

        return ConvertTimestamp(value, fieldName, errors);
    }

    private static DateTimeOffset? OptionalTimestamp(
        Timestamp? value,
        string fieldName,
        List<string> errors) => value is null ? null : ConvertTimestamp(value, fieldName, errors);

    private static DateTimeOffset? ConvertTimestamp(
        Timestamp value,
        string fieldName,
        List<string> errors)
    {
        try
        {
            return value.ToDateTimeOffset();
        }
        catch (InvalidOperationException)
        {
            errors.Add($"{fieldName} is invalid.");
            return null;
        }
    }

    private static void RequireBound(
        int count,
        int minimum,
        int maximum,
        string fieldName,
        List<string> errors)
    {
        if (count < minimum || count > maximum)
        {
            errors.Add($"{fieldName} must contain between {minimum} and {maximum} entries.");
        }
    }
}
