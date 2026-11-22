using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApp.Client.Core;

/// <summary>Typed client for the peer's loopback HTTP API. Only maps HTTP to DTOs and errors; no UI concerns.</summary>
public sealed class LocalApiClient : IPeerDataSource, IAlertSource, ISecuritySource, IDisposable
{
    public const int DefaultMaxPages = 20;
    public const int PageSize = 1000;
    public static readonly Uri DefaultAddress = new("http://127.0.0.1:55432/");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly bool ownsHttp;

    public LocalApiClient(HttpClient http, bool ownsHttp = false, ILocalPresenceProvider? presence = null)
    {
        Presence = presence;
        ArgumentNullException.ThrowIfNull(http);
        if (http.BaseAddress is null)
        {
            throw new ArgumentException("The HttpClient needs a BaseAddress.", nameof(http));
        }

        this.http = http;
        this.ownsHttp = ownsHttp;
    }

    /// <summary>
    /// Verifies the person for high-risk actions (approve pairing, update, grants) when the node enforces presence.
    /// Settable so the UI can supply it after construction; null means such actions fail with
    /// <see cref="LocalPresenceRequiredException"/> once the node enforces presence.
    /// </summary>
    public ILocalPresenceProvider? Presence { get; set; }

    public Uri Address => http.BaseAddress!;

    public string Description => Address.ToString();

    /// <summary>The loopback API has no push channel; callers poll.</summary>
    public bool SupportsLiveUpdates => false;

#pragma warning disable CS0067 // Never raised: HTTP polling only.
    public event EventHandler? StateChanged;
#pragma warning restore CS0067

    /// <summary>Creates a client with the cookie handling the antiforgery scheme needs.</summary>
    public static LocalApiClient Create(Uri address, TimeSpan? timeout = null, ILocalPresenceProvider? presence = null)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var client = new HttpClient(handler)
        {
            BaseAddress = address,
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
        return new LocalApiClient(client, ownsHttp: true, presence);
    }

    public Task<RuntimeDto> GetRuntimeAsync(CancellationToken ct = default) => GetAsync<RuntimeDto>("api/runtime", ct);

    public Task<IReadOnlyList<TrustedPeerDto>> GetPeersAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<TrustedPeerDto>>("api/peers", ct);

    public Task<IReadOnlyList<PendingPairingDto>> GetPairingsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PendingPairingDto>>("api/pairings", ct);

    public async Task<IReadOnlyList<AlertDto>> GetAlertsAsync(int limit = 100, CancellationToken ct = default) =>
        (await GetAsync<AlertsEnvelope>(Url("api/v1/alerts", [("limit", limit.ToString(CultureInfo.InvariantCulture))]), ct)
            .ConfigureAwait(false)).Alerts;

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
        PostAsync($"api/pairings/{Uri.EscapeDataString(pairingId)}/approve", ct, presenceAction: "pairing.approve", presenceSubject: pairingId);

    /// <summary>Approves the staged update; needs presence verification when the node enforces it. <paramref name="targetVersion"/> is the staged version the node reports.</summary>
    public Task ApproveUpdateAsync(string targetVersion, CancellationToken ct = default) =>
        PostAsync("api/v1/update/approve", ct, body: new { }, presenceAction: "update.approve", presenceSubject: targetVersion);

    /// <summary>Sets a paired peer's remote-control grants. Granting anything needs presence verification when enforced.</summary>
    public Task SetControlGrantAsync(string peerId, bool monitoringControls, bool pairingApproval, CancellationToken ct = default) =>
        SetControlGrantAsync(peerId, monitoringControls, pairingApproval, updateApproval: false, ct);

    /// <summary>Sets a paired peer's remote-control grants including remote update approval (T044).</summary>
    public Task SetControlGrantAsync(string peerId, bool monitoringControls, bool pairingApproval, bool updateApproval, CancellationToken ct = default) =>
        PostAsync(
            $"api/v1/control/grants/{Uri.EscapeDataString(peerId)}",
            ct,
            body: new { monitoringControls, pairingApproval, updateApproval },
            presenceAction: monitoringControls || pairingApproval || updateApproval ? "control.grant" : null,
            presenceSubject: FormattableString.Invariant($"{peerId}|monitoring={monitoringControls}|pairing={pairingApproval}|update={updateApproval}"));

    /// <summary>Every stored control grant. Peers with no stored grant are default-deny and are not listed.</summary>
    public Task<IReadOnlyList<ControlGrantDto>> GetControlGrantsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ControlGrantDto>>("api/v1/control/grants", ct);

    /// <summary>The grant for a peer: the stored one, the all-false default for a known peer without one, or null for a peer this node does not know.</summary>
    public async Task<ControlGrantDto?> GetControlGrantAsync(string peerId, CancellationToken ct = default)
    {
        var stored = (await GetControlGrantsAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(item => string.Equals(item.PeerId, peerId, StringComparison.Ordinal));
        if (stored is not null)
        {
            return stored;
        }

        var peers = await GetPeersAsync(ct).ConfigureAwait(false);
        return peers.Any(item => string.Equals(item.PeerId, peerId, StringComparison.Ordinal))
            ? ControlGrantDto.Default(peerId)
            : null;
    }

    /// <summary>Phone presence-key enrollments waiting for approval (newest first), with the key fingerprint to compare on the phone.</summary>
    public Task<IReadOnlyList<PresenceEnrollmentDto>> ListPendingPresenceEnrollmentsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PresenceEnrollmentDto>>("api/v1/control/presence-enrollments", ct);

    /// <summary>Approves a phone's fingerprint-key enrollment; needs presence verification when enforced.</summary>
    public Task ApprovePhonePresenceEnrollmentAsync(string enrollmentId, CancellationToken ct = default) =>
        PostAsync(
            $"api/v1/control/presence-enrollments/{Uri.EscapeDataString(enrollmentId)}/approve",
            ct,
            presenceAction: "presence-enrollment.approve",
            presenceSubject: enrollmentId);

    /// <summary>Whether desktop fingerprint verification is set up and enforced on the node.</summary>
    public Task<LocalPresenceStatusDto> GetLocalPresenceStatusAsync(CancellationToken ct = default) =>
        GetAsync<LocalPresenceStatusDto>("api/v1/local-presence", ct);

    /// <summary>
    /// Enrolls this client's presence key with the node. The person is verified, and the new key proves possession by
    /// signing the node's challenge. Trust on first use: only the first enrollment is accepted.
    /// </summary>
    public async Task<LocalPresenceStatusDto> EnrollLocalPresenceAsync(CancellationToken ct = default)
    {
        var provider = Presence is { IsAvailable: true } available
            ? available
            : throw new LocalPresenceRequiredException("Fingerprint verification is not available on this computer.");
        var spki = await provider.GetPublicKeySpkiAsync(ct).ConfigureAwait(false)
            ?? throw new LocalPresenceRequiredException("The presence key could not be created.");
        var proof = await ObtainProofAsync(LocalPresenceProtocol.EnrollAction, LocalPresenceProtocol.Fingerprint(spki), ct)
            .ConfigureAwait(false);
        return await PostJsonAsync<LocalPresenceStatusDto>(
            "api/v1/local-presence/enrollment",
            new { publicKeySpki = Convert.ToBase64String(spki), challengeId = proof.ChallengeId, signature = proof.Signature },
            ct).ConfigureAwait(false);
    }

    /// <summary>Removes the enrolled presence key; requires a proof from that same key.</summary>
    public async Task<LocalPresenceStatusDto> RemoveLocalPresenceAsync(CancellationToken ct = default)
    {
        var status = await GetLocalPresenceStatusAsync(ct).ConfigureAwait(false);
        if (!status.Enrolled || status.FingerprintSha256 is null)
        {
            throw new ApiValidationException("No presence key is enrolled.");
        }

        var proof = await ObtainProofAsync(LocalPresenceProtocol.RemoveAction, status.FingerprintSha256, ct).ConfigureAwait(false);
        return await PostJsonAsync<LocalPresenceStatusDto>(
            "api/v1/local-presence/removal",
            new { challengeId = proof.ChallengeId, signature = proof.Signature },
            ct).ConfigureAwait(false);
    }

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

    private async Task PostAsync(
        string url,
        CancellationToken ct,
        object? body = null,
        string? presenceAction = null,
        string? presenceSubject = null)
    {
        // The antiforgery token is bound to the cookie issued with it; fetch a fresh pair per mutation and
        // retry once on 403 in case the node restarted and rotated its keys. A presence_required refusal triggers
        // one verification and retry with the proof; a proof is never reused.
        PresenceProof? proof = null;
        var csrfRetried = false;
        for (; ; )
        {
            var token = await GetCsrfTokenAsync(ct).ConfigureAwait(false);
            var attached = proof;
            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("X-CSRF-TOKEN", token);
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body, options: Json);
                }

                if (attached is not null)
                {
                    request.Headers.Add("X-Presence-Challenge", attached.ChallengeId);
                    request.Headers.Add("X-Presence-Signature", attached.Signature);
                }

                return request;
            }, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var code = await ReadErrorCodeAsync(response, ct).ConfigureAwait(false);
                if (code is "presence_required" or "presence_invalid" or "presence_not_enrolled")
                {
                    if (code == "presence_not_enrolled")
                    {
                        throw new LocalPresenceRefusedException(
                            "The node requires fingerprint verification, but none is set up. Set it up first.");
                    }

                    if (code == "presence_invalid" || proof is not null || presenceAction is null)
                    {
                        throw new LocalPresenceRefusedException("The node refused the fingerprint verification. Try again.");
                    }

                    proof = await ObtainProofAsync(presenceAction, presenceSubject ?? string.Empty, ct).ConfigureAwait(false);
                    continue;
                }

                if (!csrfRetried)
                {
                    csrfRetried = true;
                    continue;
                }
            }

            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            return;
        }
    }

    private async Task<T> PostJsonAsync<T>(string url, object body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetCsrfTokenAsync(ct).ConfigureAwait(false);
            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
                request.Headers.Add("X-CSRF-TOKEN", token);
                return request;
            }, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0 &&
                await ReadErrorCodeAsync(response, ct).ConfigureAwait(false) is null)
            {
                continue;
            }

            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden)
            {
                throw new LocalPresenceRefusedException(await ReadErrorAsync(response, ct).ConfigureAwait(false));
            }

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
    }

    /// <summary>Asks the node for a challenge, checks it is exactly what was requested, has the person verified, and signs it.</summary>
    private async Task<PresenceProof> ObtainProofAsync(string action, string subject, CancellationToken ct)
    {
        var provider = Presence is { IsAvailable: true } available
            ? available
            : throw new LocalPresenceRequiredException(
                "This action needs fingerprint verification, which is not available in this client.");
        var challenge = await PostJsonAsync<ChallengeDto>(
            "api/v1/local-presence/challenge", new { action, subject }, ct).ConfigureAwait(false);
        var expected = LocalPresenceProtocol.BuildPayload(
            challenge.ChallengeId, challenge.Nonce, action, subject, challenge.ExpiresAtUnixSeconds);
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(challenge.Payload);
        }
        catch (FormatException exception)
        {
            throw new IncompatibleApiException("The node returned an unreadable presence challenge.", exception);
        }

        if (!string.Equals(challenge.Action, action, StringComparison.Ordinal) ||
            !string.Equals(challenge.Subject, subject, StringComparison.Ordinal) ||
            !payload.AsSpan().SequenceEqual(expected))
        {
            throw new IncompatibleApiException("The node's presence challenge does not match the requested action; nothing was signed.");
        }

        var signed = await provider.SignAsync(payload, LocalPresenceProtocol.Describe(action), ct).ConfigureAwait(false);
        return signed switch
        {
            { Outcome: LocalPresenceVerification.Verified, Signature: { Length: > 0 } signature } =>
                new PresenceProof(challenge.ChallengeId, Convert.ToBase64String(signature)),
            { Outcome: LocalPresenceVerification.Cancelled } =>
                throw new LocalPresenceCancelledException("Verification was cancelled; nothing was changed."),
            _ => throw new LocalPresenceRequiredException("Fingerprint verification is not available on this computer."),
        };
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<ErrorDto>(Json, ct).ConfigureAwait(false))?.Error;
        }
        catch (JsonException)
        {
            return null;
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

    private sealed record AlertsEnvelope(IReadOnlyList<AlertDto> Alerts);

    private sealed record CsrfDto(string RequestToken);

    private sealed record ErrorDto(string? Error);

    private sealed record PresenceProof(string ChallengeId, string Signature);

    private sealed record ChallengeDto(
        string ChallengeId, string Nonce, string Action, string Subject, long ExpiresAtUnixSeconds, string Payload);
}
