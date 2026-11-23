using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MonitorApp.Client.Core;

/// <summary>Typed client for the peer's loopback HTTP API. Only maps HTTP to DTOs and errors; no UI concerns.</summary>
public sealed class LocalApiClient : IPeerDataSource, IAlertSource, ISecuritySource, ILocalUpdatesSource, IPhonePairingHost, IThingsSource, IHistorySource, IViewingSource, IDisposable
{
    public const int DefaultMaxPages = 20;
    public const int PageSize = 1000;
    public static readonly Uri DefaultAddress = new("http://127.0.0.1:55432/");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly List<ViewingLease> viewings = [];

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

    public Task<IReadOnlyList<PeerStateTransitionDto>> GetPeerStateHistoryAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PeerStateTransitionDto>>(Url("api/v1/history/peer-states", [
            ("from", fromUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("to", toUtc.ToString("O", CultureInfo.InvariantCulture))]), ct);

    public Task<IReadOnlyList<UpdateEventDto>> GetUpdateHistoryAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<UpdateEventDto>>(Url("api/v1/history/updates", [
            ("from", fromUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("to", toUtc.ToString("O", CultureInfo.InvariantCulture))]), ct);

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

    public Task<PeerReachDto> GetPeerReachAsync(CancellationToken ct = default) => GetAsync<PeerReachDto>("api/v1/network/reach", ct);

    public async Task<PeerReachDto> SetPeerReachAsync(bool reachableByPhone, CancellationToken ct = default)
    {
        await PostAsync("api/v1/network/reach", ct, body: new { reachableByPhone },
            presenceAction: "network.reach", presenceSubject: reachableByPhone ? "tailscale" : "local").ConfigureAwait(false);
        return await GetPeerReachAsync(ct).ConfigureAwait(false);
    }

    public Task<PhonePairingDto> GetPhonePairingAsync(CancellationToken ct = default) => GetAsync<PhonePairingDto>("api/v1/phone-pairing", ct);

    public async Task<PhonePairingDto> CreatePhoneInvitationAsync(CancellationToken ct = default)
    {
        await PostAsync("api/v1/phone-pairing", ct, body: new { }).ConfigureAwait(false);
        return await GetPhonePairingAsync(ct).ConfigureAwait(false);
    }

    public async Task<ThingsDto?> GetThingsAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/v1/things"), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        try
        {
            return await response.Content.ReadFromJsonAsync<ThingsDto>(Json, ct).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new IncompatibleApiException("The node returned a response this client cannot read.", exception);
        }
    }

    public Task AddThingAsync(string text, string? kindId, CancellationToken ct = default) =>
        PostAsync("api/v1/things", ct, body: new { text, kindId });

    public Task EditThingAsync(ThingEditDto edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return PostAsync("api/v1/things/edit", ct, body: edit);
    }

    public async Task<LocalUpdateStatusDto> GetLocalUpdateStatusAsync(CancellationToken ct = default)
    {
        // A node started without its update agent (development runs, some tests) has no update API: say so plainly.
        using (var probe = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/v1/update"), ct).ConfigureAwait(false))
        {
            if (probe.StatusCode == HttpStatusCode.NotFound)
            {
                return new LocalUpdateStatusDto("approve", "unknown", "Disabled", null,
                    "this Monitor App was not installed with automatic updates.", null, null, false);
            }
        }

        return await GetAsync<LocalUpdateStatusDto>("api/v1/update", ct).ConfigureAwait(false);
    }

    public async Task<LocalUpdateStatusDto> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        await PostAsync("api/v1/update/check", ct, body: new { }).ConfigureAwait(false);
        return await GetLocalUpdateStatusAsync(ct).ConfigureAwait(false);
    }

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

    /// <summary>Acknowledges an alert on this desktop (recorded as acknowledged by "local"); idempotent, the first acknowledgement is kept.</summary>
    public Task AcknowledgeAlertAsync(string alertId, CancellationToken ct = default) =>
        PostAsync($"api/v1/alerts/{Uri.EscapeDataString(alertId)}/acknowledge", ct, body: new { });

    /// <summary>Alert preferences of every alert recipient (default: all severities, no opted-out rules).</summary>
    public Task<IReadOnlyList<AlertPreferencesDto>> GetAlertPreferencesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AlertPreferencesDto>>("api/v1/alerts/preferences", ct);

    /// <summary>Replaces a recipient's alert preferences. <paramref name="minSeverity"/>: null or "All", "Info", "Warning", "Critical".</summary>
    public Task SetAlertPreferencesAsync(
        string peerId, string? minSeverity, IReadOnlyCollection<string> mutedRuleKinds, CancellationToken ct = default) =>
        PostAsync(
            $"api/v1/alerts/preferences/{Uri.EscapeDataString(peerId)}",
            ct,
            body: new { minSeverity, mutedRuleKinds });

    /// <summary>Effective alert rules on this desktop (T054): the CPU, memory and disk rules, and peer-not-seen per trusted peer, with defaults and accepted ranges.</summary>
    public Task<IReadOnlyList<AlertRuleDto>> GetAlertRulesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AlertRuleDto>>("api/v1/alerts/rules", ct);

    /// <summary>
    /// Replaces the rule overrides for <paramref name="subjectPeerId"/> (this desktop for CPU, memory and disk; a trusted peer for
    /// peer not seen). An empty list restores the defaults. The server rejects out-of-range thresholds with 400.
    /// </summary>
    public Task SetAlertRulesAsync(
        string subjectPeerId, IReadOnlyCollection<AlertRuleOverrideDto> overrides, CancellationToken ct = default) =>
        PostAsync(
            $"api/v1/alerts/rules/{Uri.EscapeDataString(subjectPeerId)}",
            ct,
            body: new { overrides });

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

    /// <summary>Asks the node for live (fast) sampling of <paramref name="peerId"/> (null = the node's own data). Renew before the lease lapses.</summary>
    public async Task<LiveInterestLeaseDto> StartLiveInterestAsync(
        string? peerId = null, IReadOnlyCollection<string>? signalIds = null, CancellationToken ct = default)
    {
        using var response = await SendMutationAsync(
            HttpMethod.Post, "api/v1/live/interest", new { peerId, signalIds }, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await ReadLeaseAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Extends a lease. Null when the node no longer knows it (lapsed or the node restarted).</summary>
    public async Task<LiveInterestLeaseDto?> RenewLiveInterestAsync(string leaseId, CancellationToken ct = default)
    {
        using var response = await SendMutationAsync(
            HttpMethod.Post, $"api/v1/live/interest/{Uri.EscapeDataString(leaseId)}/renew", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await ReadLeaseAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Ends a lease early; an unknown lease is not an error.</summary>
    public async Task EndLiveInterestAsync(string leaseId, CancellationToken ct = default)
    {
        using var response = await SendMutationAsync(
            HttpMethod.Delete, $"api/v1/live/interest/{Uri.EscapeDataString(leaseId)}", null, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the node to sample fast while the UI is looking at <paramref name="peerId"/> (null = this node). Never
    /// throws: the lease is created and renewed in the background (and re-created if the node restarts), and ended on
    /// dispose; if the node is unreachable the lease simply lapses on its own.
    /// </summary>
    public IDisposable BeginViewing(string? peerId, IReadOnlyCollection<string>? signalIds = null)
    {
        var viewing = new ViewingLease(this, peerId, signalIds?.ToArray());
        lock (viewings)
        {
            viewings.Add(viewing);
        }

        viewing.Start();
        return viewing;
    }

    public void Dispose()
    {
        ViewingLease[] open;
        lock (viewings)
        {
            open = [.. viewings];
            viewings.Clear();
        }

        foreach (var viewing in open)
        {
            viewing.Abandon();
        }

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

    /// <summary>Sends a mutation with a fresh antiforgery pair, retrying once on 403 (a restarted node rotates its keys).</summary>
    private async Task<HttpResponseMessage> SendMutationAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetCsrfTokenAsync(ct).ConfigureAwait(false);
            var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Add("X-CSRF-TOKEN", token);
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body, options: Json);
                }

                return request;
            }, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0)
            {
                response.Dispose();
                continue;
            }

            return response;
        }
    }

    private static async Task<LiveInterestLeaseDto> ReadLeaseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<LiveInterestLeaseDto>(Json, ct).ConfigureAwait(false)
                ?? throw new IncompatibleApiException("The node returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new IncompatibleApiException("The node returned a response this client cannot read.", exception);
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

    /// <summary>One background-maintained live-interest lease: create, renew on the node's schedule, end on dispose.</summary>
    private sealed class ViewingLease(LocalApiClient owner, string? peerId, string[]? signalIds) : IDisposable
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
        private readonly CancellationTokenSource stop = new();
        private volatile bool abandoned;

        public void Start() => _ = Task.Run(RunAsync);

        public void Dispose()
        {
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>The client is going away: stop without a DELETE (the node's lease lapses on its own).</summary>
        public void Abandon()
        {
            abandoned = true;
            Dispose();
        }

        private async Task RunAsync()
        {
            var ct = stop.Token;
            string? leaseId = null;
            var delay = TimeSpan.Zero;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }

                    try
                    {
                        if (leaseId is null)
                        {
                            // Not cancelled by Dispose: if the node registers the lease while Dispose races the
                            // response, the id must still arrive here so the finally block can end it, otherwise the
                            // node would keep sampling fast until the lease lapses.
                            using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            var lease = await owner.StartLiveInterestAsync(peerId, signalIds, startTimeout.Token)
                                .ConfigureAwait(false);
                            leaseId = lease.LeaseId;
                            delay = RenewDelay(lease);
                        }
                        else if (await owner.RenewLiveInterestAsync(leaseId, ct).ConfigureAwait(false) is { } renewed)
                        {
                            delay = RenewDelay(renewed);
                        }
                        else
                        {
                            // The node lost the lease (it lapsed or the node restarted): create a new one now.
                            leaseId = null;
                            delay = TimeSpan.Zero;
                        }
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // Node unreachable or refusing: never surface into the UI; keep the lease id and try again.
                        delay = RetryDelay;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (owner.viewings)
                {
                    owner.viewings.Remove(this);
                }

                if (leaseId is not null && !abandoned)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await owner.EndLiveInterestAsync(leaseId, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Best effort: the lease lapses on its own.
                    }
                }

                stop.Dispose();
            }
        }

        private static TimeSpan RenewDelay(LiveInterestLeaseDto lease) => TimeSpan.FromSeconds(Math.Clamp(lease.RenewAfterSeconds, 1, 60));
    }

    private sealed record AlertsEnvelope(IReadOnlyList<AlertDto> Alerts);

    private sealed record CsrfDto(string RequestToken);

    private sealed record ErrorDto(string? Error);

    private sealed record PresenceProof(string ChallengeId, string Signature);

    private sealed record ChallengeDto(
        string ChallengeId, string Nonce, string Action, string Subject, long ExpiresAtUnixSeconds, string Payload);
}
