using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using MonitorApp.Peer.Core;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Node;

/// <summary>
/// Linux identity provider. The P-256 key is stored as PKCS#8 in a file created atomically with mode 0600
/// inside a 0700 directory, and loading refuses files that are group/other accessible or not owned by the
/// current user. There is no OS key wrapping (no DPAPI equivalent); protection is file permissions only.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxFileIdentityProvider : INodeIdentityProvider
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeOwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode GroupOtherMask =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private static ReadOnlySpan<byte> FileMagic => "MONITOR-LINUX-P256-V1\n"u8;
    private readonly EcdsaPeerIdentityKey key;

    private LinuxFileIdentityProvider(EcdsaPeerIdentityKey key, PeerPublicIdentity publicIdentity)
    {
        this.key = key;
        PublicIdentity = publicIdentity;
    }

    public PeerPublicIdentity PublicIdentity { get; }

    public IPeerIdentityKey IdentityKey => key;

    public static async Task<LinuxFileIdentityProvider> LoadOrCreateAsync(
        string keyPath,
        PeerStore store,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The Linux file identity provider requires Linux.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentNullException.ThrowIfNull(store);
        var fullPath = Path.GetFullPath(keyPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory, DirectoryMode);

        byte[] privateKey;
        if (File.Exists(fullPath))
        {
            privateKey = await ReadExistingAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            privateKey = generated.ExportPkcs8PrivateKey();
            var file = new byte[FileMagic.Length + privateKey.Length];
            FileMagic.CopyTo(file);
            privateKey.CopyTo(file.AsSpan(FileMagic.Length));
            var temp = Path.Combine(directory, $".identity-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var output = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                    UnixCreateMode = FileModeOwnerOnly,
                }))
                {
                    await output.WriteAsync(file, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // overwrite:false never clobbers a key a concurrent starter published first.
                File.Move(temp, fullPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                CryptographicOperations.ZeroMemory(privateKey);
                privateKey = await ReadExistingAsync(fullPath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(file);
                File.Delete(temp);
            }
        }

        try
        {
            var (key, publicIdentity) = await NodeIdentityProvider.BindAsync(
                privateKey, store, $"linux-file:{fullPath}", cancellationToken).ConfigureAwait(false);
            return new LinuxFileIdentityProvider(key, publicIdentity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    public void Dispose() => key.Dispose();

    private static async Task<byte[]> ReadExistingAsync(string path, CancellationToken cancellationToken)
    {
        EnsureSafeKeyFile(path);
        var file = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!file.AsSpan().StartsWith(FileMagic))
            {
                throw new CryptographicException("The peer identity key file has an unsupported format.");
            }

            return file.AsSpan(FileMagic.Length).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(file);
        }
    }

    private static void EnsureSafeKeyFile(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new CryptographicException($"The peer identity key file '{path}' must not be a symbolic link.");
        }

        var mode = File.GetUnixFileMode(path);
        if ((mode & GroupOtherMask) != 0)
        {
            throw new CryptographicException(
                $"The peer identity key file '{path}' has unsafe permissions {Convert.ToString((int)mode, 8)}; " +
                "it must be accessible by its owner only (chmod 600).");
        }

        var owner = GetOwnerUid(path);
        var current = geteuid();
        if (owner != current)
        {
            throw new CryptographicException(
                $"The peer identity key file '{path}' is owned by uid {owner}, not the current user (uid {current}).");
        }
    }

    private static uint GetOwnerUid(string path)
    {
        // statx has a fixed, architecture-independent layout: stx_uid is a u32 at offset 20.
        const int AtFdCwd = -100;
        const int AtSymlinkNoFollow = 0x100;
        const uint StatxUid = 0x8;
        var buffer = new byte[256];
        var nativePath = System.Text.Encoding.UTF8.GetBytes(path + "\0");
        if (statx(AtFdCwd, nativePath, AtSymlinkNoFollow, StatxUid, buffer) != 0)
        {
            throw new IOException(
                $"Unable to determine the owner of '{path}' (errno {Marshal.GetLastPInvokeError()}).");
        }

        return BitConverter.ToUInt32(buffer, 20);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, byte[] pathname, int flags, uint mask, byte[] statxbuf);
}
