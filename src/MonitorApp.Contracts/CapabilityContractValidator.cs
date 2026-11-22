using Google.Protobuf.WellKnownTypes;
using MonitorApp.Contracts.V1;

namespace MonitorApp.Contracts;

public static class CapabilityContractRules
{
    public const int MaximumEntityTypes = 256;
    public const int MaximumObservations = 4096;
    public const int MaximumRelationships = 1024;
    public const int MaximumInspectors = 256;
}

public static class CapabilityContractValidator
{
    public static ContractValidationResult Validate(CapabilityModuleDeclaration? declaration)
    {
        var errors = new List<string>();
        if (declaration is null)
        {
            return ContractValidationResult.Failure(["Capability module declaration is required."]);
        }

        RequireNamespacedId(declaration.ModuleId, "Module id", errors);
        if (declaration.ModuleVersion is null || declaration.ModuleVersion.Major == 0)
        {
            errors.Add("Module semantic version major must be greater than zero.");
        }

        ValidateCompatibility(declaration.CompatiblePeerProtocol, errors);
        RequireEnum(declaration.ModuleKind, "Module kind", errors);
        RequireBound(declaration.EntityTypes.Count, 1, CapabilityContractRules.MaximumEntityTypes, "Entity types", errors);
        RequireBound(declaration.Observations.Count, 1, CapabilityContractRules.MaximumObservations, "Observations", errors);
        RequireBound(declaration.Relationships.Count, 0, CapabilityContractRules.MaximumRelationships, "Relationships", errors);
        RequireBound(declaration.Inspectors.Count, 1, CapabilityContractRules.MaximumInspectors, "Inspectors", errors);

        var entityIds = UniqueIds(
            declaration.EntityTypes.Select(item => item.EntityTypeId),
            "Entity type id",
            errors);
        foreach (var entity in declaration.EntityTypes)
        {
            RequireNamespacedId(entity.EntityTypeId, "Entity type id", errors);
            RequireText(entity.DisplayName, "Entity display name", errors);
        }

        var signalIds = UniqueIds(
            declaration.Observations.Select(item => item.SignalId),
            "Observation signal id",
            errors);
        foreach (var observation in declaration.Observations)
        {
            ValidateObservation(declaration.ModuleKind, observation, entityIds, errors);
        }

        _ = UniqueIds(
            declaration.Relationships.Select(item => item.RelationshipTypeId),
            "Relationship type id",
            errors);
        foreach (var relationship in declaration.Relationships)
        {
            RequireNamespacedId(relationship.RelationshipTypeId, "Relationship type id", errors);
            RequireDeclaredEntity(relationship.SourceEntityTypeId, entityIds, "Relationship source entity type", errors);
            RequireDeclaredEntity(relationship.TargetEntityTypeId, entityIds, "Relationship target entity type", errors);
        }

        _ = UniqueIds(
            declaration.Inspectors.Select(item => item.InspectorId),
            "Inspector id",
            errors);
        var fallbackCount = 0;
        foreach (var inspector in declaration.Inspectors)
        {
            RequireNamespacedId(inspector.InspectorId, "Inspector id", errors);
            RequireText(inspector.Title, "Inspector title", errors);
            RequireEnum(inspector.Kind, "Inspector kind", errors);
            if (inspector.Kind == InspectorKind.GeneratedFallback)
            {
                fallbackCount++;
            }

            if (inspector.SignalIds.Count == 0)
            {
                errors.Add("Inspector descriptors must reference at least one declared signal.");
            }

            foreach (var signalId in inspector.SignalIds)
            {
                if (!signalIds.Contains(signalId))
                {
                    errors.Add("Inspector signal ids must reference observations in the same declaration.");
                }
            }
        }

        if (fallbackCount != 1)
        {
            errors.Add("A capability declaration must contain exactly one truthful generated fallback inspector.");
        }

        return errors.Count == 0
            ? ContractValidationResult.Success()
            : ContractValidationResult.Failure(errors);
    }

    private static void ValidateObservation(
        CapabilityModuleKind moduleKind,
        ObservationDeclaration observation,
        HashSet<string> entityIds,
        List<string> errors)
    {
        RequireNamespacedId(observation.SignalId, "Observation signal id", errors);
        RequireDeclaredEntity(observation.EntityTypeId, entityIds, "Observation entity type", errors);
        RequireNamespacedId(observation.Domain, "Observation domain", errors);
        RequireNamespacedId(observation.Subsystem, "Observation subsystem", errors);
        RequireNamespacedId(observation.Source, "Observation source", errors);
        RequireEnum(observation.ValueKind, "Observation value kind", errors);
        RequireEnum(observation.TemporalBehavior, "Observation temporal behavior", errors);
        RequireEnum(observation.Importance, "Observation importance", errors);
        RequireEnum(observation.Privacy, "Observation privacy", errors);
        RequireEnum(observation.CollectionCost, "Observation collection cost", errors);
        RequireEnum(observation.NetworkDataClass, "Network data class", errors);

        if (observation.ValueKind is ObservationValueKind.Numeric or ObservationValueKind.Counter &&
            string.IsNullOrWhiteSpace(observation.Unit))
        {
            errors.Add("Numeric and counter observations require an explicit unit.");
        }

        var sampleInterval = OptionalPositiveDuration(
            observation.DefaultSamplingInterval,
            "Default sampling interval",
            errors);
        var staleAfter = RequiredPositiveDuration(observation.StaleAfter, "Stale-after duration", errors);
        _ = RequiredPositiveDuration(observation.DefaultRetention, "Default retention duration", errors);
        if (observation.TemporalBehavior is
                ObservationTemporalBehavior.Gauge or
                ObservationTemporalBehavior.MonotonicCounter or
                ObservationTemporalBehavior.DiscreteState &&
            sampleInterval is null)
        {
            errors.Add("Sampled observations require a default sampling interval.");
        }

        if (sampleInterval.HasValue && staleAfter.HasValue && sampleInterval > staleAfter)
        {
            errors.Add("Default sampling interval cannot exceed the stale-after duration.");
        }

        if (observation.Privacy == ObservationPrivacyClass.Sensitive &&
            !observation.RequiresExplicitEnablement)
        {
            errors.Add("Sensitive observations require explicit enablement.");
        }

        if (moduleKind == CapabilityModuleKind.OrdinaryMonitoring)
        {
            if (observation.Privacy == ObservationPrivacyClass.DefensiveSecurity)
            {
                errors.Add("Ordinary monitoring cannot declare defensive-security observations.");
            }

            if (IsProhibitedOrdinaryNetworkData(observation.NetworkDataClass))
            {
                errors.Add("Ordinary monitoring cannot declare communication identities, traffic detail, query history, per-process traffic, or content.");
            }
        }
        else if (moduleKind == CapabilityModuleKind.DefensiveSecurity)
        {
            if (observation.Privacy != ObservationPrivacyClass.DefensiveSecurity ||
                !observation.RequiresExplicitEnablement ||
                !observation.RequiresEvidence ||
                !observation.RequiresConfidence)
            {
                errors.Add("Defensive-security observations require separate enablement, defensive privacy, evidence, and confidence.");
            }
        }
    }

    private static bool IsProhibitedOrdinaryNetworkData(NetworkDataClass value) => value is
        NetworkDataClass.CommunicationIdentity or
        NetworkDataClass.FlowOrSession or
        NetworkDataClass.DnsOrQueryHistory or
        NetworkDataClass.PacketDetail or
        NetworkDataClass.PerApplicationOrProcess or
        NetworkDataClass.Content;

    private static void ValidateCompatibility(ProtocolCompatibility? compatibility, List<string> errors)
    {
        if (compatibility is null || compatibility.MinimumMajor == 0 || compatibility.MaximumMajor == 0)
        {
            errors.Add("Compatible peer protocol range is required.");
            return;
        }

        if (compatibility.MaximumMajor < compatibility.MinimumMajor ||
            compatibility.MaximumMajor == compatibility.MinimumMajor &&
            compatibility.MaximumMinor < compatibility.MinimumMinor)
        {
            errors.Add("Compatible peer protocol range is invalid.");
            return;
        }

        var supportsCurrent = compatibility.MinimumMajor <= PeerContractRules.ProtocolMajor &&
                              compatibility.MaximumMajor >= PeerContractRules.ProtocolMajor &&
                              (compatibility.MinimumMajor < PeerContractRules.ProtocolMajor ||
                               compatibility.MinimumMinor <= PeerContractRules.BilateralSessionProofMinor) &&
                              (compatibility.MaximumMajor > PeerContractRules.ProtocolMajor ||
                               compatibility.MaximumMinor >= PeerContractRules.BilateralSessionProofMinor);
        if (!supportsCurrent)
        {
            errors.Add("Capability declaration does not support the current peer protocol.");
        }
    }

    private static HashSet<string> UniqueIds(
        IEnumerable<string> values,
        string fieldName,
        List<string> errors)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!result.Add(value))
            {
                errors.Add($"{fieldName} values must be unique.");
            }
        }

        return result;
    }

    private static void RequireDeclaredEntity(
        string value,
        HashSet<string> entityIds,
        string fieldName,
        List<string> errors)
    {
        RequireNamespacedId(value, fieldName, errors);
        if (!entityIds.Contains(value))
        {
            errors.Add($"{fieldName} must reference an entity type in the same declaration.");
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

    private static void RequireBound(int count, int minimum, int maximum, string fieldName, List<string> errors)
    {
        if (count < minimum || count > maximum)
        {
            errors.Add($"{fieldName} must contain between {minimum} and {maximum} entries.");
        }
    }

    private static TimeSpan? RequiredPositiveDuration(
        Duration? value,
        string fieldName,
        List<string> errors)
    {
        if (value is null)
        {
            errors.Add($"{fieldName} is required.");
            return null;
        }

        return PositiveDuration(value, fieldName, errors);
    }

    private static TimeSpan? OptionalPositiveDuration(
        Duration? value,
        string fieldName,
        List<string> errors) => value is null ? null : PositiveDuration(value, fieldName, errors);

    private static TimeSpan? PositiveDuration(Duration value, string fieldName, List<string> errors)
    {
        try
        {
            var result = value.ToTimeSpan();
            if (result <= TimeSpan.Zero)
            {
                errors.Add($"{fieldName} must be positive.");
                return null;
            }

            return result;
        }
        catch (InvalidOperationException)
        {
            errors.Add($"{fieldName} is invalid.");
            return null;
        }
        catch (OverflowException)
        {
            errors.Add($"{fieldName} is invalid.");
            return null;
        }
    }
}
