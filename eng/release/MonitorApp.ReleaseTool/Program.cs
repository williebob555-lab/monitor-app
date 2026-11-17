using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MonitorApp.Update;

namespace MonitorApp.ReleaseTool;

internal static partial class Program
{
    private const string PrivateKeyEnvironmentVariable =
        "MONITOR_UPDATE_SIGNING_KEY_PKCS8_BASE64";
    private const string KeyIdEnvironmentVariable = "MONITOR_UPDATE_SIGNING_KEY_ID";
    private const string PublicKeyEnvironmentVariable =
        "MONITOR_UPDATE_PUBLIC_KEY_SPKI_BASE64";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                throw new ArgumentException("Expected the create or generate-key command.");
            }

            var options = ParseOptions(args[1..]);
            if (string.Equals(args[0], "generate-key", StringComparison.Ordinal))
            {
                await GenerateKeyAsync(options).ConfigureAwait(false);
                return 0;
            }

            if (!string.Equals(args[0], "create", StringComparison.Ordinal))
            {
                throw new ArgumentException("Expected the create or generate-key command.");
            }

            var version = Required(options, "version");
            var releaseVersion = ReleaseVersion.Parse(version);
            var minimumVersion = ReleaseVersion.Parse(Required(options, "minimum-compatible-version"));
            var channel = Required(options, "channel");
            var publishedAt = DateTimeOffset.Parse(
                Required(options, "published-at"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var artifactDirectory = Path.GetFullPath(Required(options, "artifacts-dir"));
            var baseUrl = new Uri(Required(options, "base-url"), UriKind.Absolute);
            var output = Path.GetFullPath(Required(options, "output"));

            var artifacts = await ReadArtifactsAsync(
                artifactDirectory,
                baseUrl,
                version).ConfigureAwait(false);
            var manifest = new UpdateManifest(
                UpdateManifestCodec.CurrentSchemaVersion,
                channel,
                releaseVersion,
                minimumVersion,
                publishedAt,
                artifacts);

            using var signingKey = LoadAndValidateSigningKey();
            var keyId = RequiredEnvironmentVariable(KeyIdEnvironmentVariable);
            var envelope = UpdateManifestCodec.CreateSignedEnvelope(manifest, keyId, signingKey);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, envelope).ConfigureAwait(false);

            Console.WriteLine($"Created signed update manifest for {artifacts.Count} artifact(s).");
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                FormatException or
                IOException or
                CryptographicException or
                UpdateVerificationException)
        {
            Console.Error.WriteLine($"Release manifest creation failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task GenerateKeyAsync(Dictionary<string, string> options)
    {
        var privateKeyPath = Path.GetFullPath(Required(options, "private-key-output"));
        var publicKeyPath = Path.GetFullPath(Required(options, "public-key-output"));
        if (string.Equals(privateKeyPath, publicKeyPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Private and public key outputs must be different files.");
        }

        if (File.Exists(privateKeyPath) || File.Exists(publicKeyPath))
        {
            throw new IOException("Release key output already exists; refusing to overwrite it.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(publicKeyPath)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = key.ExportPkcs8PrivateKey();
        try
        {
            await WriteNewTextFileAsync(
                privateKeyPath,
                Convert.ToBase64String(privateKey)).ConfigureAwait(false);
            await WriteNewTextFileAsync(
                publicKeyPath,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }

        Console.WriteLine("Generated a P-256 release key pair without printing private material.");
        Console.WriteLine($"Protect and remove the private key file after configuring GitHub: {privateKeyPath}");
    }

    private static async Task WriteNewTextFileAsync(string path, string value)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(value).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static async Task<IReadOnlyList<UpdateArtifact>> ReadArtifactsAsync(
        string artifactDirectory,
        Uri baseUrl,
        string version)
    {
        if (!Directory.Exists(artifactDirectory))
        {
            throw new DirectoryNotFoundException("Artifact directory does not exist.");
        }

        if (baseUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Artifact base URL must use HTTPS.");
        }

        var artifacts = new List<UpdateArtifact>();
        foreach (var path in Directory.EnumerateFiles(artifactDirectory).Order(StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            var match = ArtifactFileName().Match(fileName);
            if (!match.Success)
            {
                continue;
            }

            if (!string.Equals(match.Groups["version"].Value, version, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Artifact {fileName} does not match release version.");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
            var url = new Uri(baseUrl, Uri.EscapeDataString(fileName));
            artifacts.Add(new UpdateArtifact(
                match.Groups["component"].Value,
                match.Groups["platform"].Value,
                match.Groups["architecture"].Value,
                url,
                stream.Length,
                Convert.ToHexStringLower(hash)));
        }

        if (artifacts.Count == 0)
        {
            throw new ArgumentException("No versioned Monitor App artifacts were found.");
        }

        return artifacts;
    }

    private static ECDsa LoadAndValidateSigningKey()
    {
        var privateKeyBytes = Convert.FromBase64String(
            RequiredEnvironmentVariable(PrivateKeyEnvironmentVariable));
        var expectedPublicKey = Convert.FromBase64String(
            RequiredEnvironmentVariable(PublicKeyEnvironmentVariable));

        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(privateKeyBytes, out var bytesRead);
            if (bytesRead != privateKeyBytes.Length || key.KeySize != 256)
            {
                throw new CryptographicException("Update signing key must be one P-256 PKCS#8 key.");
            }

            var actualPublicKey = key.ExportSubjectPublicKeyInfo();
            if (!CryptographicOperations.FixedTimeEquals(actualPublicKey, expectedPublicKey))
            {
                throw new CryptographicException(
                    "Configured update public key does not match the signing key.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyBytes);
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Release options must use --name value pairs.");
            }

            var name = args[index][2..];
            if (!options.TryAdd(name, args[index + 1]))
            {
                throw new ArgumentException($"Release option --{name} was repeated.");
            }
        }

        return options;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Release option --{name} is required.");

    private static string RequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"Required release environment variable {name} is missing.");

    [GeneratedRegex(
        "^monitorapp-(?<component>[a-z0-9.]+)-(?<platform>[a-z0-9.]+)-(?<architecture>[a-z0-9.]+)-(?<version>[0-9]+\\.[0-9]+\\.[0-9]+)\\.(?:zip|apk|tar\\.gz)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactFileName();
}
