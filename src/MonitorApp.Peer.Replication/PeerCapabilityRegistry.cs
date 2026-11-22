using System.Security.Cryptography;
using Google.Protobuf;
using MonitorApp.Contracts;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Service;

public sealed class CapabilityRegistrationException(IReadOnlyList<string> errors)
    : Exception("Capability module declaration is invalid.")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class PeerCapabilityRegistry(
    PeerStore store,
    TimeProvider? timeProvider = null)
{
    public const int MaximumDeclarationBytes = 1024 * 1024;

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<CapabilityDescriptor> RegisterAsync(
        CapabilityModuleDeclaration declaration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        cancellationToken.ThrowIfCancellationRequested();
        var validation = CapabilityContractValidator.Validate(declaration);
        if (!validation.IsValid)
        {
            throw new CapabilityRegistrationException(validation.Errors);
        }

        var descriptor = declaration.ToByteArray();
        if (descriptor.Length > MaximumDeclarationBytes)
        {
            throw new CapabilityRegistrationException(["Capability declaration exceeds the configured byte limit."]);
        }

        var lastSeparator = declaration.ModuleId.LastIndexOf('.');
        var @namespace = declaration.ModuleId[..lastSeparator];
        var version = $"{declaration.ModuleVersion.Major}.{declaration.ModuleVersion.Minor}.{declaration.ModuleVersion.Patch}";
        await store.UpsertCapabilityAsync(new CapabilityCatalogEntry(
            declaration.ModuleId,
            @namespace,
            version,
            descriptor,
            RequireUtc(clock.GetUtcNow())), cancellationToken).ConfigureAwait(false);
        return new CapabilityDescriptor
        {
            ModuleId = declaration.ModuleId,
            ModuleVersion = version,
            DeclarationSha256 = ByteString.CopyFrom(SHA256.HashData(descriptor)),
        };
    }

    public async Task<CapabilityModuleDeclaration?> ReadAsync(
        string moduleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        var stored = (await store.ReadCapabilitiesAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => string.Equals(item.CapabilityId, moduleId, StringComparison.Ordinal));
        if (stored is null)
        {
            return null;
        }

        CapabilityModuleDeclaration declaration;
        try
        {
            declaration = CapabilityModuleDeclaration.Parser.ParseFrom(stored.Descriptor);
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new CapabilityRegistrationException(["Stored capability declaration cannot be decoded."])
            {
                Source = exception.Source,
            };
        }

        var validation = CapabilityContractValidator.Validate(declaration);
        var expectedVersion = $"{declaration.ModuleVersion.Major}.{declaration.ModuleVersion.Minor}.{declaration.ModuleVersion.Patch}";
        if (!validation.IsValid || declaration.ModuleId != stored.CapabilityId || expectedVersion != stored.Version)
        {
            throw new CapabilityRegistrationException(
                validation.IsValid
                    ? ["Stored capability declaration identity does not match its catalog entry."]
                    : validation.Errors);
        }

        return declaration;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new InvalidOperationException("Capability registry clock must return UTC.");
}
