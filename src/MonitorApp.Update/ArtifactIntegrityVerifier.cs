using System.Security.Cryptography;

namespace MonitorApp.Update;

public static class ArtifactIntegrityVerifier
{
    public static async Task VerifyFileAsync(
        string path,
        UpdateArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(artifact);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length != artifact.Size)
        {
            throw new UpdateVerificationException(
                UpdateVerificationError.ArtifactSizeMismatch,
                "Downloaded update size does not match the signed manifest.");
        }

        var actualHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var expectedHash = Convert.FromHexString(artifact.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new UpdateVerificationException(
                UpdateVerificationError.ArtifactHashMismatch,
                "Downloaded update hash does not match the signed manifest.");
        }
    }
}
