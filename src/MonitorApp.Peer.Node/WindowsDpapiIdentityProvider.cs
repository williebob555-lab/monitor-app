using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Node;

public sealed class WindowsDpapiIdentityProvider : ILocalPeerIdentityProvider, IDisposable
{
    private static ReadOnlySpan<byte> FileMagic => "MONITOR-DPAPI-P256-V1\n"u8;
    private readonly EcdsaPeerIdentityKey key;

    private WindowsDpapiIdentityProvider(EcdsaPeerIdentityKey key, PeerPublicIdentity publicIdentity)
    {
        this.key = key;
        PublicIdentity = publicIdentity;
    }

    public PeerPublicIdentity PublicIdentity { get; }

    public IPeerIdentityKey IdentityKey => key;

    public static async Task<WindowsDpapiIdentityProvider> LoadOrCreateAsync(
        string keyPath,
        PeerStore store,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The preview node currently requires Windows DPAPI.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentNullException.ThrowIfNull(store);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);

        byte[] privateKey;
        if (File.Exists(keyPath))
        {
            var protectedFile = await File.ReadAllBytesAsync(keyPath, cancellationToken).ConfigureAwait(false);
            if (!protectedFile.AsSpan().StartsWith(FileMagic))
            {
                throw new CryptographicException("The peer identity key file has an unsupported format.");
            }

            privateKey = Unprotect(protectedFile.AsSpan(FileMagic.Length));
        }
        else
        {
            using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            privateKey = generated.ExportPkcs8PrivateKey();
            var protectedKey = Protect(privateKey);
            var file = new byte[FileMagic.Length + protectedKey.Length];
            FileMagic.CopyTo(file);
            protectedKey.CopyTo(file.AsSpan(FileMagic.Length));
            try
            {
                await using var output = new FileStream(
                    keyPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await output.WriteAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (File.Exists(keyPath))
            {
                CryptographicOperations.ZeroMemory(privateKey);
                var protectedFile = await File.ReadAllBytesAsync(keyPath, cancellationToken).ConfigureAwait(false);
                if (!protectedFile.AsSpan().StartsWith(FileMagic))
                {
                    throw new CryptographicException("The peer identity key file has an unsupported format.");
                }

                privateKey = Unprotect(protectedFile.AsSpan(FileMagic.Length));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(file);
                CryptographicOperations.ZeroMemory(protectedKey);
            }
        }

        try
        {
            var key = EcdsaPeerIdentityKey.ImportPkcs8(privateKey);
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
                    $"windows-dpapi:{Path.GetFullPath(keyPath)}",
                    $"origin-{idSuffix}",
                    1,
                    DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
            else if (existing.PeerId != publicIdentity.PeerId ||
                     !CryptographicOperations.FixedTimeEquals(existing.PublicKeyFingerprint, fingerprint))
            {
                key.Dispose();
                throw new CryptographicException("The protected identity key does not match the peer database.");
            }

            return new WindowsDpapiIdentityProvider(key, publicIdentity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public void Dispose() => key.Dispose();

    private static byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext, protect: true);

    private static byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        var inputCopy = input.ToArray();
        try
        {
            Marshal.Copy(inputCopy, 0, inputPointer, input.Length);
            var inputBlob = new DataBlob { Size = input.Length, Data = inputPointer };
            DataBlob outputBlob;
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob);
            if (!succeeded)
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[outputBlob.Size];
                Marshal.Copy(outputBlob.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                for (var index = 0; index < outputBlob.Size; index++)
                {
                    Marshal.WriteByte(outputBlob.Data, index, 0);
                }

                LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputCopy);
            for (var index = 0; index < input.Length; index++)
            {
                Marshal.WriteByte(inputPointer, index, 0);
            }

            Marshal.FreeHGlobal(inputPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
