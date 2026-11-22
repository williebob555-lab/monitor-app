using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApp.Client.Core;

/// <summary>Typed client for the peer's loopback HTTP API. Only maps HTTP to DTOs and errors; no UI concerns.</summary>
public sealed class LocalApiClient : IPeerDataSource, IDisposable
{
    public const int DefaultMaxPages = 20;
    public const int PageSize = 1000;
    public static readonly Uri DefaultAddress = new("http://127.0.0.1:55432/");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly bool ownsHttp;

    public LocalApiClient(HttpClient http, bool ownsHttp = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.BaseAddress is null)
        {
            throw new ArgumentException("The HttpClient needs a BaseAddress.", nameof(http));
        }

        this.http = http;
        this.ownsHttp = ownsHttp;
    }

    public Uri Address => http.BaseAddress!;

    public string Description => Address.ToString();

    /// <summary>The loopback API has no push channel; callers poll.</summary>
    public bool SupportsLiveUpdates => false;

#pragma warning disable CS0067 // Never raised: HTTP polling only.
    public event EventHandler? StateChanged;
#pragma warning restore CS0067

    /// <summary>Creates a client with the cookie handling the antiforgery scheme needs.</summary>
    public static LocalApiClient Create(Uri address, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var client = new HttpClient(handler)
        {
            BaseAddress = address,
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
        return new LocalApiClient(client, ownsHttp: true);
    }

    public Task<RuntimeDto> GetRuntimeAsync(CancellationToken ct = default) => GetAsync<RuntimeDto>("api/runtime", ct);

    public Task<IReadOnlyList<TrustedPeerDto>> GetPeersAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<TrustedPeerDto>>("api/peers", ct);

    public Task<IReadOnlyList<PendingPairingDto>> GetPairingsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PendingPairingDto>>("api/pairings", ct);

    public Task<CatalogDto> GetCatalogAsync(CancellationToken ct = default) => GetAsync<CatalogDto>("api/v1/catalog", ct);

    /// <summary>Reads state as of <paramref name="asOf"/>, or the node's current time when null.</summary>
    public Task<StateDto> GetStateAsync(
        DateTimeOffset? asOf = null,
        int? lookbackSeconds = null,
        string? subjectEntityId = null,
        string? signalId = null,
        CancellationToken ct = default) =>
        GetAsync<StateDto>(Url("api/v1/state", [
            ("asOf", asOf?.ToString("O", CultureInfo.InvariantCulture)),
            ("lookbackSeconds", lookbackSeconds?.ToString(CultureInfo.InvariantCulture)),
            ("subjectEntityId", subjectEntityId),
            ("signalId", signalId)]), ct);

    public Task<HistoryPageDto> GetHistoryPageAsync(
        HistoryQuery query, string? cursor, int limit = PageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetAsync<HistoryPageDto>(Url("api/v1/history", [
            ("from", query.From.ToString("O", CultureInfo.InvariantCulture)),
            ("to", query.To.ToString("O", CultureInfo.InvariantCulture)),
            ("subjectEntityId", query.SubjectEntityId),
            ("signalId", query.SignalId),
            ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ("cursor", cursor)]), ct);
    }

    /// <summary>Follows <c>nextCursor</c> until exhausted or <paramref name="maxPages"/> pages were read.</summary>
    public async Task<HistoryResult> GetHistoryAsync(
        HistoryQuery query, int maxPages = DefaultMaxPages, int pageSize = PageSize, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);
        var points = new List<HistoryPointDto>();
        string? cursor = null;
        var resolution = "raw";
        var pages = 0;
        while (true)
        {
            var page = await GetHistoryPageAsync(query, cursor, pageSize, ct).ConfigureAwait(false);
            pages++;
            resolution = page.Resolution;
            points.AddRange(page.Points);
            cursor = page.NextCursor;
            if (cursor is null || !page.Truncated)
            {
                return new HistoryResult(points, resolution, pages, MoreAvailable: false);
            }

            if (pages >= maxPages)
            {
                return new HistoryResult(points, resolution, pages, MoreAvailable: true);
            }
        }
    }

    public Task ApprovePairingAsync(string pairingId, CancellationToken ct = default) =>
        PostAsync($"api/pairings/{Uri.EscapeDataString(pairingId)}/approve", ct);

    public Task RejectPairingAsync(string pairingId, CancellationToken ct = default) =>
        PostAsync($"api/pairings/{Uri.EscapeDataString(pairingId)}/reject", ct);

    public Task RevokePeerAsync(string peerId, CancellationToken ct = default) =>
        PostAsync($"api/peers/{Uri.EscapeDataString(peerId)}/revoke", ct);

    public void Dispose()
    {
        if (ownsHttp)
        {
            http.Dispose();
        }
    }

    private static string Url(string path, (string Name, string? Value)[] query)
    {
        var parts = query.Where(pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => $"{pair.Name}={Uri.EscapeDataString(pair.Value!)}");
        var joined = string.Join('&', parts);
        return joined.Length == 0 ? path : $"{path}?{joined}";
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
                ?? throw new IncompatibleApiException("The node returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new IncompatibleApiException("The node returned a response this client cannot read.", exception);
        }
    }

    private async Task PostAsync(string url, CancellationToken ct)
    {
        // The antiforgery token is bound to the cookie issued with it; fetch a fresh pair per mutation and
        // retry once on 403 in case the node restarted and rotated its keys.
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetCsrfTokenAsync(ct).ConfigureAwait(false);
            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("X-CSRF-TOKEN", token);
                return request;
            }, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0)
            {
                continue;
            }

            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            return;
        }
    }

    private async Task<string> GetCsrfTokenAsync(CancellationToken ct)
    {
        var csrf = await GetAsync<CsrfDto>("api/csrf", ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(csrf.RequestToken)
            ? throw new IncompatibleApiException("The node returned an empty antiforgery token.")
            : csrf.RequestToken;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken ct)
    {
        try
        {
            using var request = create();
            return await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new NodeUnreachableException(Address, exception);
        }
        catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new NodeUnreachableException(Address, exception);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new ApiValidationException(await ReadErrorAsync(response, ct).ConfigureAwait(false));
        }

        throw new IncompatibleApiException(response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "The node refused the change (antiforgery check failed).",
            HttpStatusCode.NotFound => "The node does not offer this API; it may be an older or different version.",
            _ => $"The node answered HTTP {(int)response.StatusCode}.",
        });
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorDto>(Json, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return error.Error;
            }
        }
        catch (JsonException)
        {
            // Fall through to the generic message.
        }

        return "The node rejected the request as invalid.";
    }

    private sealed record CsrfDto(string RequestToken);

    private sealed record ErrorDto(string? Error);
}
