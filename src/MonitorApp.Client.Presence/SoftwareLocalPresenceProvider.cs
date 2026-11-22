using System.Security.Cryptography;
using MonitorApp.Client.Core;

namespace MonitorApp.Client.Presence;

/// <summary>Where the software presence key lives; separated so tests and platforms can choose storage.</summary>
public interface ILocalPresenceKeyStore
{
    /// <summary>PKCS#8 private key bytes, or null when no key exists yet.</summary>
    byte[]? Load();

    void Save(byte[] pkcs8);
}

/// <summary>
/// Owner-only key file (0600 on Unix, in a 0700 directory). On Windows prefer <c>WindowsHelloPresenceProvider</c>, whose
/// key is hardware-backed; this file is only as protected as the user's profile ACL.
/// </summary>
public sealed class FileLocalPresenceKeyStore(string path) : ILocalPresenceKeyStore
{
    public byte[]? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
        {
            throw new InvalidOperationException($"The presence key file '{path}' is readable by other users; refusing to use it.");
        }

        return File.ReadAllBytes(path);
    }

    public void Save(byte[] pkcs8)
    {
        ArgumentNullException.ThrowIfNull(pkcs8);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(pkcs8);
    }
}

/// <summary>
/// ECDSA P-256 presence key held by the desktop client process and released for signing only after a platform
/// <see cref="ILocalUserPresence"/> check succeeds (Windows Hello UserConsentVerifier, fprintd). The check gates the
/// signature in this process; a same-user process that can read the key file could sign without it (see SECURITY.md),
/// which is why Windows prefers the hardware-backed key credential provider.
/// </summary>
public sealed class SoftwareLocalPresenceProvider(ILocalUserPresence verifier, ILocalPresenceKeyStore keys, string methodName)
    : ILocalPresenceProvider, IDisposable
{
    private readonly object gate = new();
    private ECDsa? key;

    public bool IsAvailable => verifier.IsAvailable;

    public string MethodName => methodName;

    public Task<byte[]?> GetPublicKeySpkiAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return Task.FromResult<byte[]?>(null);
        }

        lock (gate)
        {
            return Task.FromResult<byte[]?>(Key().ExportSubjectPublicKeyInfo());
        }
    }

    public async Task<LocalPresenceSignature> SignAsync(byte[] payload, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var verification = await verifier.VerifyAsync(reason, cancellationToken).ConfigureAwait(false);
        if (verification != LocalPresenceVerification.Verified)
        {
            return new LocalPresenceSignature(verification, null);
        }

        lock (gate)
        {
            return new LocalPresenceSignature(
                LocalPresenceVerification.Verified,
                Key().SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            key?.Dispose();
            key = null;
        }
    }

    private ECDsa Key()
    {
        if (key is not null)
        {
            return key;
        }

        var loaded = ECDsa.Create();
        var stored = keys.Load();
        if (stored is null)
        {
            loaded.Dispose();
            loaded = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            keys.Save(loaded.ExportPkcs8PrivateKey());
        }
        else
        {
            loaded.ImportPkcs8PrivateKey(stored, out _);
        }

        key = loaded;
        return key;
    }
}

/// <summary>Chooses the platform's verifier for the cross-platform part; Windows composes its own provider.</summary>
public static class LocalPresenceProviders
{
    /// <summary>Linux: fprintd-gated software key under <paramref name="keyPath"/>; elsewhere: the unavailable provider.</summary>
    public static ILocalPresenceProvider CreateDefault(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        if (OperatingSystem.IsLinux())
        {
            var fprintd = new FprintdUserPresence();
            if (fprintd.IsAvailable)
            {
                return new SoftwareLocalPresenceProvider(fprintd, new FileLocalPresenceKeyStore(keyPath), "Fingerprint (fprintd)");
            }
        }

        return new UnavailableLocalPresenceProvider();
    }
}
