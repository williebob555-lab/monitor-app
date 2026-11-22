namespace MonitorApp.Update;

public interface IManifestFetcher
{
    Task<byte[]> FetchAsync(CancellationToken cancellationToken);
}

public interface IArtifactDownloader
{
    Task DownloadAsync(Uri url, Stream destination, long maximumBytes, CancellationToken cancellationToken);
}

/// <summary>HTTPS-only bounded manifest fetch (e.g. a GitHub Releases asset URL). Trust comes from the signature, not TLS.</summary>
public sealed class HttpManifestFetcher : IManifestFetcher
{
    private readonly HttpClient _client;
    private readonly Uri _url;

    public HttpManifestFetcher(HttpClient client, Uri manifestUrl)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(manifestUrl);
        HttpsOnly.Require(manifestUrl);
        _client = client;
        _url = manifestUrl;
    }

    public async Task<byte[]> FetchAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await BoundedCopy.CopyAsync(body, buffer, UpdateManifestCodec.MaximumEnvelopeSize, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}

public sealed class HttpArtifactDownloader : IArtifactDownloader
{
    private readonly HttpClient _client;

    public HttpArtifactDownloader(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task DownloadAsync(Uri url, Stream destination, long maximumBytes, CancellationToken cancellationToken)
    {
        HttpsOnly.Require(url);
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await BoundedCopy.CopyAsync(body, destination, maximumBytes, cancellationToken).ConfigureAwait(false);
    }
}

internal static class HttpsOnly
{
    public static void Require(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateVerificationException(UpdateVerificationError.InvalidManifest, "Update sources must use HTTPS.");
        }
    }
}

internal static class BoundedCopy
{
    public static async Task CopyAsync(Stream source, Stream destination, long maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                throw new UpdateVerificationException(UpdateVerificationError.ArtifactSizeMismatch, "Download exceeds the size allowed by the signed manifest.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
