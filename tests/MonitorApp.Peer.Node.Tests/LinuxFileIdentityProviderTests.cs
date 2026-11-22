using System.Runtime.Versioning;
using System.Security.Cryptography;
using MonitorApp.Peer.Node;
using MonitorApp.Peer.Persistence;
using Xunit;

namespace MonitorApp.Peer.Node.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinuxFileIdentityProviderTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Fact]
    [Trait("Category", "Linux")]
    public async Task CreatesKeyFileOwnerOnlyInPrivateDirectoryAndIsRestartStable()
    {
        if (!OperatingSystem.IsLinux()) return;
        var parent = CreateTemporaryDirectory();
        try
        {
            var dataDirectory = Path.Combine(parent, "data");
            var keyPath = Path.Combine(dataDirectory, "identity.p256");
            var store = await CreateStoreAsync(parent);
            string firstPeerId;
            using (var first = await LinuxFileIdentityProvider.LoadOrCreateAsync(keyPath, store))
            {
                firstPeerId = first.PublicIdentity.PeerId;
            }

            Assert.Equal(OwnerOnly, File.GetUnixFileMode(keyPath));
            Assert.Equal(OwnerOnly | UnixFileMode.UserExecute, File.GetUnixFileMode(dataDirectory));
            Assert.Single(Directory.GetFileSystemEntries(dataDirectory));
            var raw = await File.ReadAllBytesAsync(keyPath);
            using var parser = ECDsa.Create();
            Assert.ThrowsAny<CryptographicException>(() => parser.ImportPkcs8PrivateKey(raw, out _));

            using var second = await LinuxFileIdentityProvider.LoadOrCreateAsync(keyPath, store);
            Assert.Equal(firstPeerId, second.PublicIdentity.PeerId);
            var signature = second.IdentityKey.Sign([1, 2, 3]);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(second.PublicIdentity.PublicKeySpki, out _);
            Assert.True(verifier.VerifyData(
                new byte[] { 1, 2, 3 },
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    [Trait("Category", "Linux")]
    public async Task LoadRefusesGroupOrOtherAccessibleKeyFile(UnixFileMode unsafeMode)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = CreateTemporaryDirectory();
        try
        {
            var keyPath = Path.Combine(root, "identity.p256");
            var store = await CreateStoreAsync(root);
            using (await LinuxFileIdentityProvider.LoadOrCreateAsync(keyPath, store))
            {
            }

            File.SetUnixFileMode(keyPath, unsafeMode);
            var error = await Assert.ThrowsAsync<CryptographicException>(
                () => LinuxFileIdentityProvider.LoadOrCreateAsync(keyPath, store));
            Assert.Contains("unsafe permissions", error.Message, StringComparison.Ordinal);

            File.SetUnixFileMode(keyPath, OwnerOnly);
            using var recovered = await LinuxFileIdentityProvider.LoadOrCreateAsync(keyPath, store);
            Assert.StartsWith("peer-", recovered.PublicIdentity.PeerId, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Linux")]
    public async Task LoadRefusesUnknownFileFormat()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = CreateTemporaryDirectory();
        try
        {
            var keyPath = Path.Combine(root, "identity.p256");
            await File.WriteAllBytesAsync(keyPath, [1, 2, 3]);
            File.SetUnixFileMode(keyPath, OwnerOnly);
            var store = await CreateStoreAsync(root);
            await Assert.ThrowsAsync<CryptographicException>(
                () => LinuxFileIdentityProvider.LoadOrCreateAsync(keyPath, store));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Linux")]
    public async Task NodeIdentityProviderUsesLinuxKeyFileName()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = CreateTemporaryDirectory();
        try
        {
            var store = await CreateStoreAsync(root);
            using var identity = await NodeIdentityProvider.LoadOrCreateAsync(root, store);
            Assert.True(File.Exists(Path.Combine(root, "identity.p256")));
            Assert.IsType<LinuxFileIdentityProvider>(identity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<PeerStore> CreateStoreAsync(string root)
    {
        Directory.CreateDirectory(root);
        var store = new PeerStore(PeerStoreOptions.Create(Path.Combine(root, "peer.db")));
        await store.InitializeAsync();
        return store;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "monitor-node-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
