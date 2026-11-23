using System.Text.Json;

namespace MonitorApp.Update;

/// <summary>
/// Finds the newest non-draft preview pre-release of a repository through the GitHub REST API and downloads its
/// <c>update-manifest.json</c>. HTTPS only. Authenticity comes from the manifest signature, never from GitHub or TLS.
/// </summary>
public sealed class GitHubPreviewManifestFetcher : IManifestFetcher
{
    public const string ManifestAssetName = "update-manifest.json";
    public const string TagPrefix = "preview-";

    private readonly HttpClient _client;
    private readonly Uri _apiBase;
    private readonly string _repository;

    public GitHubPreviewManifestFetcher(HttpClient client, Uri apiBase, string repository)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(apiBase);
        ArgumentNullException.ThrowIfNull(repository);
        if (apiBase.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The release API must use HTTPS.", nameof(apiBase));
        }

        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || !part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException("Repository must be owner/name.", nameof(repository));
        }

        _client = client;
        _apiBase = apiBase;
        _repository = repository;
    }

    public async Task<byte[]> FetchAsync(CancellationToken cancellationToken)
    {
        var listUrl = new Uri(_apiBase, $"repos/{_repository}/releases?per_page=20");
        using var request = new HttpRequestMessage(HttpMethod.Get, listUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await BoundedCopy(body, buffer, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(buffer.ToArray());

        Uri? manifestUrl = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()
                || !release.GetProperty("prerelease").GetBoolean()
                || release.GetProperty("tag_name").GetString() is not { } tag
                || !tag.StartsWith(TagPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == ManifestAssetName
                    && Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url))
                {
                    manifestUrl = url;
                    break;
                }
            }

            if (manifestUrl is not null)
            {
                break;
            }
        }

        if (manifestUrl is null)
        {
            throw new HttpRequestException("No preview release with an update manifest was found.");
        }

        return await new HttpManifestFetcher(_client, manifestUrl).FetchAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task BoundedCopy(Stream source, Stream destination, int maximum, CancellationToken cancellationToken)
    {
        var chunk = new byte[16 * 1024];
        var total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maximum)
            {
                throw new HttpRequestException("Release listing is larger than allowed.");
            }

            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
