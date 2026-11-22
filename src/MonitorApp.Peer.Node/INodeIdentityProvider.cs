using System.Security.Cryptography;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Node;

/// <summary>A local peer identity whose private key is protected by an OS-specific mechanism.</summary>
public interface INodeIdentityProvider : ILocalPeerIdentityProvider, IDisposable
{
}

public static class NodeIdentityProvider
{
    /// <summary>Loads or creates the identity for the current OS under <paramref name="dataDirectory"/>.</summary>
    public static async Task<INodeIdentityProvider> LoadOrCreateAsync(
        string dataDirectory,
        PeerStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (OperatingSystem.IsWindows())
        {
            return await WindowsDpapiIdentityProvider.LoadOrCreateAsync(
                Path.Combine(dataDirectory, "identity.dpapi"), store, cancellationToken).ConfigureAwait(false);
        }

        if (OperatingSystem.IsLinux())
        {
            return await LinuxFileIdentityProvider.LoadOrCreateAsync(
                Path.Combine(dataDirectory, "identity.p256"), store, cancellationToken).ConfigureAwait(false);
        }

        throw new PlatformNotSupportedException("The preview node supports only Windows and Linux.");
    }

    /// <summary>
    /// Builds the key and public identity from PKCS#8 bytes and records or verifies the local identity row.
    /// The caller still owns (and must zero) <paramref name="privateKey"/>.
    /// </summary>
    internal static async Task<(EcdsaPeerIdentityKey Key, PeerPublicIdentity PublicIdentity)> BindAsync(
        byte[] privateKey,
        PeerStore store,
        string keyDescriptor,
        CancellationToken cancellationToken)
    {
        var key = EcdsaPeerIdentityKey.ImportPkcs8(privateKey);
        try
        {
            var publicKey = key.ExportSubjectPublicKeyInfo();
            var fingerprint = SHA256.HashData(publicKey);
            var idSuffix = Convert.ToHexString(fingerprint.AsSpan(0, 10)).ToLowerInvariant();
            var publicIdentity = new PeerPublicIdentity($"peer-{idSuffix}", publicKey, fingerprint);
            var existing = await store.ReadLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                await store.SetLocalIdentityAsync(new LocalPeerIdentity(
                    publicIdentity.PeerId,
                    publicKey,
                    fingerprint,
                    keyDescriptor,
                    $"origin-{idSuffix}",
                    1,
                    DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
            else if (existing.PeerId != publicIdentity.PeerId ||
                     !CryptographicOperations.FixedTimeEquals(existing.PublicKeyFingerprint, fingerprint))
            {
                throw new CryptographicException("The protected identity key does not match the peer database.");
            }

            return (key, publicIdentity);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
