using MonitorApp.Contracts.V1;

namespace MonitorApp.Contracts;

public sealed record CapabilityInspectorSelection(
    InspectorDescriptor Descriptor,
    bool IsGeneratedFallback);

public static class CapabilityInspectorSelector
{
    public static CapabilityInspectorSelection Select(
        CapabilityModuleDeclaration declaration,
        string entityTypeId,
        IReadOnlySet<string> availablePurposeBuiltInspectorIds)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityTypeId);
        ArgumentNullException.ThrowIfNull(availablePurposeBuiltInspectorIds);

        var validation = CapabilityContractValidator.Validate(declaration);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Capability declaration is invalid: {string.Join("; ", validation.Errors)}",
                nameof(declaration));
        }

        if (!declaration.EntityTypes.Any(entity =>
                string.Equals(entity.EntityTypeId, entityTypeId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Entity type is not declared by the capability module.",
                nameof(entityTypeId));
        }

        var entitySignalIds = declaration.Observations
            .Where(observation => string.Equals(
                observation.EntityTypeId,
                entityTypeId,
                StringComparison.Ordinal))
            .Select(observation => observation.SignalId)
            .ToHashSet(StringComparer.Ordinal);

        var purposeBuilt = declaration.Inspectors
            .Where(inspector =>
                inspector.Kind == InspectorKind.PurposeBuilt &&
                availablePurposeBuiltInspectorIds.Contains(inspector.InspectorId) &&
                inspector.SignalIds.Any(entitySignalIds.Contains))
            .OrderBy(inspector => inspector.InspectorId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (purposeBuilt is not null)
        {
            return new CapabilityInspectorSelection(purposeBuilt.Clone(), IsGeneratedFallback: false);
        }

        var fallback = declaration.Inspectors.Single(inspector =>
            inspector.Kind == InspectorKind.GeneratedFallback);
        return new CapabilityInspectorSelection(fallback.Clone(), IsGeneratedFallback: true);
    }
}
