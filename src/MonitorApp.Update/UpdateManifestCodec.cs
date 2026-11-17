using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorApp.Update;

public static class UpdateManifestCodec
{
    public const int CurrentSchemaVersion = 1;
    public const string SignatureAlgorithm = "ECDSA_P256_SHA256_P1363";
    public const int MaximumEnvelopeSize = 1_048_576;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    public static byte[] CreateSignedEnvelope(
        UpdateManifest manifest,
        string keyId,
        ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(privateKey);
        RequireP256Key(privateKey, "Update signing key must be ECDSA P-256.");

        var payload = SerializePayload(ValidateAndNormalize(manifest));
        var signature = privateKey.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (signature.Length != 64)
        {
            throw new CryptographicException("Update signing key must be ECDSA P-256.");
        }

        var envelope = new EnvelopeDto(
            CurrentSchemaVersion,
            keyId,
            SignatureAlgorithm,
            Convert.ToBase64String(payload),
            Convert.ToBase64String(signature));
        return JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
    }

    public static UpdateManifest VerifyAndRead(
        ReadOnlySpan<byte> envelopeBytes,
        TrustedUpdateKey trustedKey)
    {
        ArgumentNullException.ThrowIfNull(trustedKey);
        if (envelopeBytes.IsEmpty || envelopeBytes.Length > MaximumEnvelopeSize)
        {
            throw Error(UpdateVerificationError.InvalidEnvelope, "Update envelope size is invalid.");
        }

        EnvelopeDto envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EnvelopeDto>(envelopeBytes, SerializerOptions)
                ?? throw Error(UpdateVerificationError.InvalidEnvelope, "Update envelope is required.");
        }
        catch (JsonException exception)
        {
            throw Error(UpdateVerificationError.InvalidEnvelope, "Update envelope JSON is invalid.", exception);
        }

        if (envelope.SchemaVersion != CurrentSchemaVersion)
        {
            throw Error(UpdateVerificationError.InvalidEnvelope, "Update envelope schema is unsupported.");
        }

        if (!string.Equals(envelope.KeyId, trustedKey.KeyId, StringComparison.Ordinal))
        {
            throw Error(UpdateVerificationError.UntrustedKey, "Update manifest key is not trusted.");
        }

        if (!string.Equals(envelope.Algorithm, SignatureAlgorithm, StringComparison.Ordinal))
        {
            throw Error(
                UpdateVerificationError.UnsupportedSignatureAlgorithm,
                "Update manifest signature algorithm is unsupported.");
        }

        var payload = DecodeBase64(envelope.Payload, "payload");
        var signature = DecodeBase64(envelope.Signature, "signature");
        if (signature.Length != 64)
        {
            throw Error(UpdateVerificationError.InvalidSignature, "Update manifest signature is invalid.");
        }

        try
        {
            using var publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(trustedKey.PublicKeySpki, out var bytesRead);
            if (bytesRead != trustedKey.PublicKeySpki.Length)
            {
                throw Error(UpdateVerificationError.UntrustedKey, "Trusted update key is invalid.");
            }

            RequireP256Key(publicKey, "Trusted update key is invalid.");
            if (!CryptographicOperations.FixedTimeEquals(
                    publicKey.ExportSubjectPublicKeyInfo(),
                    trustedKey.PublicKeySpki))
            {
                throw Error(UpdateVerificationError.UntrustedKey, "Trusted update key is not canonical.");
            }

            if (!publicKey.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw Error(UpdateVerificationError.InvalidSignature, "Update manifest signature is invalid.");
            }
        }
        catch (CryptographicException exception)
        {
            throw Error(UpdateVerificationError.UntrustedKey, "Trusted update key is invalid.", exception);
        }

        try
        {
            var payloadDto = JsonSerializer.Deserialize<PayloadDto>(payload, SerializerOptions)
                ?? throw Error(UpdateVerificationError.InvalidManifest, "Update manifest payload is required.");
            return ValidateAndNormalize(FromDto(payloadDto));
        }
        catch (JsonException exception)
        {
            throw Error(UpdateVerificationError.InvalidManifest, "Update manifest JSON is invalid.", exception);
        }
        catch (FormatException exception)
        {
            throw Error(UpdateVerificationError.InvalidManifest, "Update manifest version is invalid.", exception);
        }
        catch (ArgumentException exception)
        {
            throw Error(UpdateVerificationError.InvalidManifest, "Update manifest content is invalid.", exception);
        }
    }

    internal static byte[] SerializePayload(UpdateManifest manifest)
    {
        var artifacts = manifest.Artifacts
            .OrderBy(artifact => artifact.Component, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Platform, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Architecture, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Url.AbsoluteUri, StringComparer.Ordinal)
            .Select(artifact => new ArtifactDto(
                artifact.Component,
                artifact.Platform,
                artifact.Architecture,
                artifact.Url.AbsoluteUri,
                artifact.Size,
                artifact.Sha256))
            .ToArray();
        var payload = new PayloadDto(
            manifest.SchemaVersion,
            manifest.Channel,
            manifest.Version.ToString(),
            manifest.MinimumCompatibleVersion.ToString(),
            manifest.PublishedAtUtc.ToUniversalTime(),
            artifacts);
        return JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
    }

    private static UpdateManifest FromDto(PayloadDto payload)
    {
        var artifacts = payload.Artifacts?.Select(artifact =>
        {
            if (!Uri.TryCreate(artifact.Url, UriKind.Absolute, out var url))
            {
                throw Error(UpdateVerificationError.InvalidManifest, "Update artifact URL is invalid.");
            }

            return new UpdateArtifact(
                artifact.Component,
                artifact.Platform,
                artifact.Architecture,
                url,
                artifact.Size,
                artifact.Sha256);
        }).ToArray() ?? [];
        return new UpdateManifest(
            payload.SchemaVersion,
            payload.Channel,
            ReleaseVersion.Parse(payload.Version),
            ReleaseVersion.Parse(payload.MinimumCompatibleVersion),
            payload.PublishedAtUtc,
            artifacts);
    }

    private static UpdateManifest ValidateAndNormalize(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            throw Error(UpdateVerificationError.InvalidManifest, "Update manifest schema is unsupported.");
        }

        if (!IsToken(manifest.Channel))
        {
            throw Error(UpdateVerificationError.InvalidManifest, "Update channel is invalid.");
        }

        if (manifest.MinimumCompatibleVersion > manifest.Version)
        {
            throw Error(
                UpdateVerificationError.InvalidManifest,
                "Minimum compatible version cannot exceed the release version.");
        }

        if (manifest.PublishedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Error(UpdateVerificationError.InvalidManifest, "Published time must be UTC.");
        }

        if (manifest.Artifacts.Count is < 1 or > 64)
        {
            throw Error(UpdateVerificationError.InvalidManifest, "Update manifest artifact count is invalid.");
        }

        var normalizedArtifacts = new List<UpdateArtifact>(manifest.Artifacts.Count);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in manifest.Artifacts)
        {
            if (!IsToken(artifact.Component) || !IsToken(artifact.Platform) || !IsToken(artifact.Architecture))
            {
                throw Error(UpdateVerificationError.InvalidManifest, "Update artifact target is invalid.");
            }

            if (!artifact.Url.IsAbsoluteUri || artifact.Url.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(artifact.Url.UserInfo))
            {
                throw Error(UpdateVerificationError.InvalidManifest, "Update artifact URL must use HTTPS.");
            }

            if (artifact.Size <= 0)
            {
                throw Error(UpdateVerificationError.InvalidManifest, "Update artifact size must be positive.");
            }

            if (string.IsNullOrEmpty(artifact.Sha256))
            {
                throw Error(UpdateVerificationError.InvalidManifest, "Update artifact SHA-256 is invalid.");
            }

            var normalizedHash = artifact.Sha256.ToLowerInvariant();
            if (normalizedHash.Length != 64 || normalizedHash.Any(character => !Uri.IsHexDigit(character)))
            {
                throw Error(UpdateVerificationError.InvalidManifest, "Update artifact SHA-256 is invalid.");
            }

            var target = $"{artifact.Component}\0{artifact.Platform}\0{artifact.Architecture}";
            if (!targets.Add(target))
            {
                throw Error(UpdateVerificationError.InvalidManifest, "Update artifact target is duplicated.");
            }

            normalizedArtifacts.Add(artifact with { Sha256 = normalizedHash });
        }

        return manifest with
        {
            Artifacts = normalizedArtifacts
                .OrderBy(artifact => artifact.Component, StringComparer.Ordinal)
                .ThenBy(artifact => artifact.Platform, StringComparer.Ordinal)
                .ThenBy(artifact => artifact.Architecture, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static byte[] DecodeBase64(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Error(
                UpdateVerificationError.InvalidEnvelope,
                $"Update envelope {field} is invalid.");
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw Error(
                UpdateVerificationError.InvalidEnvelope,
                $"Update envelope {field} is invalid.",
                exception);
        }
    }

    private static bool IsToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');

    private static void RequireP256Key(ECDsa key, string errorMessage)
    {
        var curveOid = key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value;
        if (key.KeySize != 256 ||
            !string.Equals(curveOid, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
        {
            throw new CryptographicException(errorMessage);
        }
    }

    private static UpdateVerificationException Error(
        UpdateVerificationError error,
        string message,
        Exception? innerException = null) =>
        innerException is null
            ? new UpdateVerificationException(error, message)
            : new UpdateVerificationException(error, message, innerException);

    private sealed record EnvelopeDto(
        int SchemaVersion,
        string KeyId,
        string Algorithm,
        string Payload,
        string Signature);

    private sealed record PayloadDto(
        int SchemaVersion,
        string Channel,
        string Version,
        string MinimumCompatibleVersion,
        DateTimeOffset PublishedAtUtc,
        ArtifactDto[] Artifacts);

    private sealed record ArtifactDto(
        string Component,
        string Platform,
        string Architecture,
        string Url,
        long Size,
        string Sha256);

}
