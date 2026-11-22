using System.Net;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Channels;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using MonitorApp.Peer.Alerts;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Control;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.LocalApi;

internal sealed record LiveFeedOptions(LiveInterestRegistry? Interests)
{
    public LiveInterestLeases? Leases { get; } = Interests is null ? null : new LiveInterestLeases(Interests);
}

internal sealed record LiveInterestInput(string? PeerId, string[]? SignalIds);

internal sealed record AlertPreferencesInput(string? MinSeverity, string[]? MutedRuleKinds);

internal sealed record AlertRuleOverrideBody(string? RuleId, bool? Enabled, double? Threshold);

internal sealed record AlertRulesInput(AlertRuleOverrideBody[]? Overrides);

internal sealed record ControlGrantInput(bool MonitoringControls, bool PairingApproval, bool UpdateApproval = false);

internal sealed record LocalPresenceChallengeRequest(string? Action, string? Subject);

internal sealed record LocalPresenceProofRequest(string? PublicKeySpki, string? ChallengeId, string? Signature);

public sealed class LocalApiHost : IAsyncDisposable
{
    private const string CspNonceKey = "csp-nonce";
    private readonly WebApplication application;

    private LocalApiHost(WebApplication application)
    {
        this.application = application;
    }

    public Uri? Address { get; private set; }

    public static LocalApiHost Create(
        LocalApiOptions options,
        PeerStore peerStore,
        PairingApplicationStore pairingStore,
        PeerPairingCoordinator pairingCoordinator,
        IPeerClock clock,
        IPeerInvitationService? invitationService = null,
        PhonePairingOptions? phonePairing = null,
        AlertStore? alertStore = null,
        LiveInterestRegistry? interests = null,
        ILocalUpdateController? updates = null,
        ControlService? control = null,
        MonitoringControlState? monitoring = null,
        LocalPresenceService? localPresence = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(peerStore);
        ArgumentNullException.ThrowIfNull(pairingStore);
        ArgumentNullException.ThrowIfNull(pairingCoordinator);
        ArgumentNullException.ThrowIfNull(clock);
        options.Validate();

        // Never the working directory ("/" under systemd): see PeerHostApplication.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.ConfigureKestrel(server => server.Listen(options.BindAddress, options.Port));
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = "MonitorLocalCsrf";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            antiforgery.FormFieldName = "__RequestVerificationToken";
            antiforgery.HeaderName = "X-CSRF-TOKEN";
        });
        builder.Services.AddSingleton(peerStore);
        builder.Services.AddSingleton(pairingStore);
        builder.Services.AddSingleton(pairingCoordinator);
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton(invitationService ?? new UnavailablePeerInvitationService());
        builder.Services.AddSingleton<PeerInvitationStatusState>();
        builder.Services.AddSingleton(new LiveFeedOptions(interests));
        builder.Services.AddSingleton(new PhonePairingService(phonePairing, peerStore));
        if (alertStore is not null)
        {
            builder.Services.AddSingleton(alertStore);
        }

        if (updates is not null)
        {
            builder.Services.AddSingleton(updates);
        }

        if (control is not null)
        {
            builder.Services.AddSingleton(control);
        }

        if (monitoring is not null)
        {
            builder.Services.AddSingleton(monitoring);
        }

        if (localPresence is not null)
        {
            builder.Services.AddSingleton(localPresence);
        }

        builder.Services.AddSingleton(new LocalApiDataSource(options.DatabasePath, peerStore));
        builder.Services.AddSingleton(new PeerHistoryQueryService(peerStore, clock, options.EffectiveStaleAfter));

        var application = builder.Build();
        application.Use(async (context, next) =>
        {
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            context.Items[CspNonceKey] = nonce;
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy =
                $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'self'; " +
                "form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.CacheControl = "no-store";
            await next(context).ConfigureAwait(false);
        });
        MapEndpoints(application);
        return new LocalApiHost(application);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await application.StartAsync(cancellationToken).ConfigureAwait(false);
        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault() ??
            throw new InvalidOperationException("The local API did not publish one loopback address.");
        Address = new Uri(address);
        if (!IPAddress.TryParse(Address.Host, out var host) || !IPAddress.IsLoopback(host))
        {
            await application.StopAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The local API did not bind to loopback.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        application.Services.GetService<LiveFeedOptions>()?.Leases?.Dispose();
        await application.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The acknowledger recorded for an alert acknowledged on this desktop (phones are recorded by peer id).</summary>
    public const string LocalAcknowledger = "local";

    private static void MapEndpoints(WebApplication application)
    {
        application.MapGet("/api/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Json(new { requestToken = tokens.RequestToken });
        });
        application.MapGet("/api/runtime", async (LocalApiDataSource data, CancellationToken token) =>
            Results.Json(await data.ReadRuntimeAsync(token).ConfigureAwait(false)));
        application.MapGet("/api/peers", async (LocalApiDataSource data, CancellationToken token) =>
            Results.Json(await data.ReadPeersAsync(token).ConfigureAwait(false)));
        application.MapGet("/api/pairings", async (LocalApiDataSource data, CancellationToken token) =>
            Results.Json(await data.ReadPendingPairingsAsync(token).ConfigureAwait(false)));
        application.MapGet("/api/observations", async (
            string originPeerId,
            string originStreamId,
            ulong? firstSequence,
            int? limit,
            LocalApiDataSource data,
            CancellationToken token) =>
        {
            var first = firstSequence ?? 1;
            var take = limit ?? 100;
            if (first == 0 || take is < 1 or > 1000)
            {
                return Results.BadRequest(new { error = "firstSequence must be positive and limit must be 1 through 1000." });
            }

            return Results.Json(await data.ReadObservationsAsync(
                originPeerId, originStreamId, first, take, token).ConfigureAwait(false));
        });

        application.MapGet("/api/v1/live", StreamLiveAsync);
        application.MapGet("/api/v1/alerts", async (
            int? limit,
            IPeerClock clock,
            IServiceProvider services,
            CancellationToken token) =>
        {
            var take = limit ?? 100;
            if (take is < 1 or > 500)
            {
                return Results.BadRequest(new { error = "limit must be 1 through 500." });
            }

            var store = services.GetService<AlertStore>();
            if (store is null)
            {
                return Results.Json(new { alerts = Array.Empty<object>() });
            }

            var found = await store.ReadActiveAndRecentAsync(clock.UtcNow - TimeSpan.FromDays(7), take, token)
                .ConfigureAwait(false);
            var deliveries = (await store.ReadOutboxForAlertsAsync(found.Select(alert => alert.AlertId).ToArray(), token)
                .ConfigureAwait(false)).ToLookup(entry => entry.AlertId, StringComparer.Ordinal);
            var acknowledgements = (await store.ReadOperatorAcknowledgementsAsync(token).ConfigureAwait(false))
                .ToDictionary(item => item.AlertId, StringComparer.Ordinal);
            var escalations = await store.ReadEscalationsAsync(token).ConfigureAwait(false);
            return Results.Json(new
            {
                alerts = found.Select(alert => new
                {
                    alert.AlertId,
                    alert.Revision,
                    alert.RuleId,
                    alert.SubjectId,
                    alert.OriginPeerId,
                    severity = alert.Severity.ToString(),
                    state = alert.State.ToString(),
                    alert.Title,
                    alert.Detail,
                    alert.RaisedAt,
                    alert.ResolvedAt,

                    // Operator acknowledgement (T051): the first acknowledgement wins; acknowledgedBy is a phone's peer id
                    // or "local" for this desktop. Escalation: set once a Critical alert stayed unacknowledged too long.
                    acknowledged = acknowledgements.ContainsKey(alert.AlertId),
                    acknowledgedBy = acknowledgements.TryGetValue(alert.AlertId, out var acknowledgement) ? acknowledgement.By : null,
                    acknowledgedAt = acknowledgements.TryGetValue(alert.AlertId, out var acknowledgedWhen) ? (DateTimeOffset?)acknowledgedWhen.At : null,
                    escalated = escalations.ContainsKey(alert.AlertId),
                    escalatedAt = escalations.TryGetValue(alert.AlertId, out var escalatedWhen) ? (DateTimeOffset?)escalatedWhen : null,

                    // Per-recipient delivery of each revision: deliveredAt is when the recipient's durable-write
                    // acknowledgement arrived (null while pending).
                    deliveries = deliveries[alert.AlertId].Select(entry => new
                    {
                        entry.RecipientPeerId,
                        entry.Revision,
                        entry.Escalation,
                        entry.EnqueuedAt,
                        entry.Attempts,
                        entry.LastAttemptAt,
                        DeliveredAt = entry.AckedAt,
                    }),
                }),
            });
        });
        application.MapPost("/api/v1/alerts/{alertId}/acknowledge", async (
            string alertId,
            HttpContext context,
            IAntiforgery antiforgery,
            IPeerClock clock,
            IServiceProvider services,
            CancellationToken token) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var store = services.GetService<AlertStore>();
            if (store is null || string.IsNullOrWhiteSpace(alertId) || alertId.Length > 128)
            {
                return Results.NotFound(new { error = "No such alert." });
            }

            // Idempotent; the first acknowledgement (a phone's or this desktop's) is kept.
            if (!await store.AcknowledgeByOperatorAsync(alertId, LocalAcknowledger, clock.UtcNow, token).ConfigureAwait(false))
            {
                return Results.NotFound(new { error = "No such alert." });
            }

            var recorded = await store.ReadOperatorAcknowledgementAsync(alertId, token).ConfigureAwait(false);
            return Results.Json(new { alertId, acknowledgedBy = recorded?.By, acknowledgedAt = recorded?.At });
        });
        application.MapGet("/api/v1/alerts/preferences", async (
            IServiceProvider services,
            CancellationToken token) =>
        {
            var store = services.GetService<AlertStore>();
            if (store is null)
            {
                return Results.Json(Array.Empty<object>());
            }

            var stored = (await store.ReadPreferencesAsync(token).ConfigureAwait(false))
                .ToDictionary(item => item.RecipientPeerId, StringComparer.Ordinal);
            var recipients = (await store.ReadRecipientsAsync(token).ConfigureAwait(false)).Union(stored.Keys, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            return Results.Json(recipients.Select(peer => stored.TryGetValue(peer, out var item)
                ? new { peerId = peer, minSeverity = AlertPreferences.SeverityName(item.MinimumSeverity), mutedRuleKinds = item.MutedRuleKinds, updatedAt = (DateTimeOffset?)item.UpdatedAt }
                : new { peerId = peer, minSeverity = "All", mutedRuleKinds = (IReadOnlyList<string>)[], updatedAt = (DateTimeOffset?)null }));
        });
        application.MapPost("/api/v1/alerts/preferences/{peerId}", async (
            string peerId,
            HttpContext context,
            IAntiforgery antiforgery,
            IPeerClock clock,
            IServiceProvider services,
            CancellationToken token) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var store = services.GetService<AlertStore>();
            var body = await context.Request.ReadFromJsonAsync<AlertPreferencesInput>(token).ConfigureAwait(false);
            if (store is null || body is null || string.IsNullOrWhiteSpace(peerId) ||
                !AlertPreferences.TryParseSeverity(body.MinSeverity, out var severity) ||
                (body.MutedRuleKinds?.Length ?? 0) > 64 || (body.MutedRuleKinds ?? []).Any(kind => kind is null || kind.Length > 128))
            {
                return Results.BadRequest(new { error = "A peer id, a minimum severity of All, Info, Warning or Critical, and at most 64 rule kinds are required." });
            }

            await store.SetPreferencesAsync(peerId, severity, body.MutedRuleKinds ?? [], clock.UtcNow, token).ConfigureAwait(false);
            return Results.Json(new
            {
                peerId,
                minSeverity = AlertPreferences.SeverityName(severity),
                mutedRuleKinds = (body.MutedRuleKinds ?? []).Select(kind => kind.Trim()).Where(kind => kind.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            });
        });
        application.MapGet("/api/v1/alerts/rules", async (
            PeerStore peerStore,
            IServiceProvider services,
            CancellationToken token) =>
        {
            var store = services.GetService<AlertStore>();
            var local = await peerStore.ReadLocalIdentityAsync(token).ConfigureAwait(false);
            if (store is null || local is null)
            {
                return Results.Json(Array.Empty<object>());
            }

            var active = (await peerStore.ReadTrustedPeersAsync(token).ConfigureAwait(false))
                .Where(item => item.TrustState == PeerTrustState.Active).Select(item => item.PeerId);
            var overrides = await store.ReadRuleOverridesAsync(token).ConfigureAwait(false);
            return Results.Json(AlertRuleCatalog.Describe(services.GetService<AlertRulesConfig>() ?? AlertRulesConfig.Default, local.PeerId, active, overrides)
                .Select(item => new
                {
                    subjectPeerId = item.SubjectPeerId,
                    ruleId = item.RuleId,
                    title = item.Title,
                    unit = item.Unit,
                    defaultThreshold = item.DefaultThreshold,
                    threshold = item.Threshold,
                    enabled = item.Enabled,
                    overridden = item.Overridden,
                    minimum = item.Minimum,
                    maximum = item.Maximum,
                    updatedAt = item.UpdatedAt,
                }));
        });
        application.MapPost("/api/v1/alerts/rules/{peerId}", async (
            string peerId,
            HttpContext context,
            IAntiforgery antiforgery,
            IPeerClock clock,
            PeerStore peerStore,
            IServiceProvider services,
            CancellationToken token) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var store = services.GetService<AlertStore>();
            AlertRulesInput? body;
            try
            {
                body = await context.Request.ReadFromJsonAsync<AlertRulesInput>(token).ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {
                body = null;
            }

            var local = await peerStore.ReadLocalIdentityAsync(token).ConfigureAwait(false);
            if (store is null || local is null || body?.Overrides is null || body.Overrides.Any(item => item is null || item.Enabled is null))
            {
                return Results.BadRequest(new { error = "A peer id and a list of overrides (ruleId, enabled, optional threshold) are required." });
            }

            var inputs = body.Overrides.Select(item => new AlertRuleOverrideInput(item.RuleId ?? string.Empty, item.Enabled!.Value, item.Threshold)).ToArray();
            var active = (await peerStore.ReadTrustedPeersAsync(token).ConfigureAwait(false))
                .Where(item => item.TrustState == PeerTrustState.Active).Select(item => item.PeerId).ToHashSet(StringComparer.Ordinal);
            var problem = AlertRuleCatalog.Validate(inputs) ?? AlertRuleCatalog.ValidateSubject(peerId, local.PeerId, active, inputs);
            if (problem is not null)
            {
                return Results.BadRequest(new { error = problem });
            }

            await store.SetRuleOverridesAsync(peerId, inputs, clock.UtcNow, token).ConfigureAwait(false);
            return Results.Json(new { peerId, overrides = inputs.Select(item => new { ruleId = item.RuleId, enabled = item.Enabled, threshold = item.Threshold }) });
        });
        application.MapPost("/api/v1/live/interest", async (
            HttpContext context,
            IAntiforgery antiforgery,
            LiveFeedOptions live,
            PeerStore store,
            CancellationToken token) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (live.Interests is null || live.Leases is null)
            {
                return Results.Json(new { error = "Live interest is not available on this node." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            LiveInterestInput? body;
            try
            {
                body = context.Request.ContentLength is 0
                    ? new LiveInterestInput(null, null)
                    : await context.Request.ReadFromJsonAsync<LiveInterestInput>(token).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                body = null;
            }

            if (body is null || !LiveInterestLeases.AreValidSignals(body.SignalIds) ||
                (body.PeerId is not null && (string.IsNullOrWhiteSpace(body.PeerId) || body.PeerId.Length > 128)))
            {
                return Results.BadRequest(new { error = "At most 64 signal ids of 1 to 128 characters and a valid peer id are required." });
            }

            var local = await store.ReadLocalIdentityAsync(token).ConfigureAwait(false);
            var isLocal = body.PeerId is null || (local is not null && string.Equals(body.PeerId, local.PeerId, StringComparison.Ordinal));
            if (isLocal && local is null)
            {
                return Results.Json(new { error = "This node has no identity yet." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!isLocal)
            {
                var peer = await store.ReadTrustedPeerAsync(body.PeerId!, token).ConfigureAwait(false);
                if (peer is null || peer.TrustState != PeerTrustState.Active)
                {
                    return Results.BadRequest(new { error = "The peer is not a trusted peer of this node." });
                }
            }

            var signals = body.SignalIds?.Distinct(StringComparer.Ordinal).ToArray();
            try
            {
                var started = live.Leases.TryBegin(() => isLocal
                    ? live.Interests.WatchLocal(local!.PeerId, signals)
                    : live.Interests.WatchRemote(body.PeerId!, signals));
                return started is null
                    ? Results.Json(new { error = "Too many live interest leases." }, statusCode: StatusCodes.Status429TooManyRequests)
                    : Results.Json(new { leaseId = started.Value.LeaseId, expiresAt = started.Value.ExpiresAt, renewAfterSeconds = live.Leases.RenewAfterSeconds });
            }
            catch (InvalidOperationException)
            {
                return Results.BadRequest(new { error = "Live interest was refused (too many signals)." });
            }
        });
        application.MapPost("/api/v1/live/interest/{leaseId}/renew", async (
            string leaseId,
            HttpContext context,
            IAntiforgery antiforgery,
            LiveFeedOptions live) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return live.Leases?.Renew(leaseId) is { } expires
                ? Results.Json(new { leaseId, expiresAt = expires, renewAfterSeconds = live.Leases.RenewAfterSeconds })
                : Results.NotFound(new { error = "No such live interest lease; it may have lapsed." });
        });
        application.MapDelete("/api/v1/live/interest/{leaseId}", async (
            string leaseId,
            HttpContext context,
            IAntiforgery antiforgery,
            LiveFeedOptions live) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return live.Leases?.End(leaseId) == true ? Results.NoContent() : Results.NotFound(new { error = "No such live interest lease." });
        });
        application.MapGet("/api/v1/catalog", async (PeerHistoryQueryService history, CancellationToken token) =>
            Results.Json(await history.ReadCatalogAsync(token).ConfigureAwait(false)));
        application.MapGet("/api/v1/history", (
            string? from,
            string? to,
            string? subjectEntityId,
            string? signalId,
            string? originPeerId,
            int? limit,
            string? cursor,
            string? resolution,
            PeerHistoryQueryService history,
            CancellationToken token) => QueryAsync(() => history.QueryHistoryAsync(
                from, to, subjectEntityId, signalId, originPeerId, limit, cursor, token, resolution)));
        application.MapGet("/api/v1/state", (
            string? asOf,
            int? lookbackSeconds,
            string? subjectEntityId,
            string? signalId,
            int? limit,
            PeerHistoryQueryService history,
            CancellationToken token) => QueryAsync(() => history.ReconstructAsync(
                asOf, lookbackSeconds, subjectEntityId, signalId, limit, token)));

        application.MapGet("/api/v1/history/peer-states", async (
            string? from,
            string? to,
            PeerStore store,
            IPeerClock clock,
            CancellationToken token) =>
        {
            if (!TryReadHistoryRange(from, to, clock, out var start, out var end, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var rows = await store.ReadPeerStateTransitionsAsync(start, end, HistoryFeedCap, token).ConfigureAwait(false);
            return Results.Json(rows.Select(row => new
            {
                peerId = row.PeerId,
                state = row.State.ToString(),
                at = row.At,
                reason = row.Reason,
            }));
        });
        application.MapGet("/api/v1/history/updates", (
            string? from,
            string? to,
            IPeerClock clock,
            HttpContext context) =>
        {
            if (!TryReadHistoryRange(from, to, clock, out var start, out var end, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var events = context.RequestServices.GetService<ILocalUpdateController>() is ILocalUpdateHistory history
                ? history.GetUpdateHistory(start, end).Take(HistoryFeedCap)
                : [];
            return Results.Json(events.Select(item => new
            {
                version = item.Version,
                outcome = item.Outcome,
                at = item.At,
                detail = item.Detail,
            }));
        });
        application.MapGet("/api/v1/update", (HttpContext context) =>
            context.RequestServices.GetService<ILocalUpdateController>() is { } updates
                ? Results.Json(updates.GetStatus())
                : Results.NotFound());
        application.MapPost("/api/v1/update/approve", async (
            HttpContext context,
            IAntiforgery antiforgery,
            CancellationToken token) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.RequestServices.GetService<ILocalUpdateController>() is not { } updates)
            {
                return Results.NotFound();
            }

            var blocked = RequirePresence(
                context, LocalPresenceService.UpdateApproveAction, updates.GetStatus().TargetVersion ?? string.Empty);
            if (blocked is not null)
            {
                return blocked;
            }

            return await updates.ApproveAsync(token).ConfigureAwait(false)
                ? Results.Json(updates.GetStatus())
                : Results.Conflict(new { error = "No staged update is awaiting approval." });
        });
        application.MapPost("/api/pairings/{pairingId}/approve", async (
            string pairingId,
            HttpContext context,
            IAntiforgery antiforgery,
            PeerPairingCoordinator coordinator,
            CancellationToken token) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var blocked = RequirePresence(context, LocalPresenceService.PairingApproveAction, pairingId);
            if (blocked is not null)
            {
                return blocked;
            }

            var pairing = await coordinator.ApprovePairingAsync(pairingId, token).ConfigureAwait(false);
            return MutationResult(context, new { pairing.PairingId, state = pairing.State.ToString() });
        });
        application.MapPost("/api/pairings/{pairingId}/reject", async (
            string pairingId,
            HttpContext context,
            IAntiforgery antiforgery,
            PeerPairingCoordinator coordinator,
            CancellationToken token) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await coordinator.RejectPairingAsync(pairingId, token).ConfigureAwait(false);
            return MutationResult(context, new { pairingId, state = PairingApprovalState.Rejected.ToString() });
        });
        application.MapPost("/api/peers/{peerId}/revoke", async (
            string peerId,
            HttpContext context,
            IAntiforgery antiforgery,
            PeerStore store,
            IPeerClock clock,
            CancellationToken token) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await store.RevokeTrustedPeerAsync(peerId, clock.UtcNow, token).ConfigureAwait(false);
            return MutationResult(context, new { peerId, state = PeerTrustState.Revoked.ToString() });
        });
        MapControlEndpoints(application);
        MapLocalPresenceEndpoints(application);
        application.MapGet("/api/v1/incidents", async (PeerStore store, CancellationToken token) =>
            Results.Json((await store.ReadPinnedIncidentsAsync(token).ConfigureAwait(false)).Select(PinnedIncidentView.Of)));
        application.MapPost("/api/v1/incidents/pin", async (
            PinIncidentRequest? body,
            HttpContext context,
            IAntiforgery antiforgery,
            PeerStore store,
            IPeerClock clock,
            CancellationToken token) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (body is null ||
                string.IsNullOrWhiteSpace(body.Label) || body.Label.Length > 200 ||
                !TryParseUtc(body.From, out var from) || !TryParseUtc(body.To, out var to) || to <= from ||
                to - from > TimeSpan.FromDays(31))
            {
                return Results.BadRequest(new
                {
                    error = "Pinning needs a label (up to 200 characters) and an ISO-8601 UTC from/to window of at most 31 days.",
                });
            }

            var incident = new PinnedIncident(Guid.NewGuid().ToString("N"), body.Label.Trim(), from, to, clock.UtcNow.ToUniversalTime());
            await store.PinIncidentAsync(incident, token).ConfigureAwait(false);
            return MutationResult(context, PinnedIncidentView.Of(incident));
        });
        application.MapPost("/api/v1/incidents/{incidentId}/unpin", async (
            string incidentId,
            HttpContext context,
            IAntiforgery antiforgery,
            PeerStore store,
            CancellationToken token) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // Unpinning only makes the window prunable on the next retention pass; pruned data is never restored.
            return await store.UnpinIncidentAsync(incidentId, token).ConfigureAwait(false)
                ? MutationResult(context, new { incidentId, pinned = false })
                : Results.NotFound(new { error = "No such pinned incident." });
        });
        application.MapPost("/api/invitations", HandleInvitationAsync);
        application.MapPost("/api/phone-invitations", HandlePhoneInvitationAsync);
        application.MapGet("/", RenderPageAsync);
    }

    /// <summary>
    /// Remote-control administration on the local (loopback) API: the audit trail, per-peer grants, and approval of
    /// biometric presence-key enrollments. Mutations need the CSRF token like every other local mutation; a remote
    /// peer can never reach these endpoints (they are the local administrator's decisions).
    /// </summary>
    private static void MapControlEndpoints(WebApplication application)
    {
        application.MapGet("/api/v1/audit", async (
            int? limit,
            long? beforeId,
            string? peerId,
            IServiceProvider services,
            CancellationToken token) =>
        {
            var take = limit ?? 100;
            if (take is < 1 or > 500)
            {
                return Results.BadRequest(new { error = "limit must be 1 through 500." });
            }

            var control = services.GetService<ControlService>();
            if (control is null)
            {
                return Results.Json(new { entries = Array.Empty<object>() });
            }

            var entries = await control.Store.ReadAuditAsync(take, beforeId, peerId, token).ConfigureAwait(false);
            return Results.Json(new { entries });
        });
        application.MapGet("/api/v1/control", async (
            IPeerClock clock,
            IServiceProvider services,
            CancellationToken token) =>
        {
            var control = services.GetService<ControlService>();
            var monitoring = services.GetService<MonitoringControlState>();
            var alerts = services.GetService<AlertStore>();
            var snapshot = monitoring?.Read();
            var mutes = alerts is null
                ? []
                : await alerts.ReadActiveMutesAsync(clock.UtcNow, token).ConfigureAwait(false);
            return Results.Json(new
            {
                grants = control is null ? [] : await control.Store.ReadGrantsAsync(token).ConfigureAwait(false),
                presenceEnrollments = control is null
                    ? []
                    : (await control.Store.ReadEnrollmentsAsync(token).ConfigureAwait(false)).Select(item => new
                    {
                        item.EnrollmentId,
                        item.PeerId,
                        fingerprintSha256 = Convert.ToHexString(item.Fingerprint),
                        state = item.State.ToString(),
                        item.RequestedAt,
                        item.DecidedAt,
                    }),
                monitoring = snapshot is null
                    ? null
                    : new
                    {
                        pauses = snapshot.Pauses.Select(pair => new { scope = pair.Key, until = pair.Value }),
                        intervalOverrideSeconds = snapshot.IntervalOverride?.TotalSeconds,
                        intervalOverrideUntil = snapshot.IntervalOverrideUntil,
                    },
                alertMutes = mutes.Select(item => new { item.Recipient, item.Scope, item.Until }),
            });
        });
        application.MapGet("/api/v1/control/grants", async (
            IServiceProvider services,
            CancellationToken token) =>
        {
            var control = services.GetService<ControlService>();
            var grants = control is null ? [] : await control.Store.ReadGrantsAsync(token).ConfigureAwait(false);
            return Results.Json(grants.Select(item => new
            {
                item.PeerId,
                item.MonitoringControls,
                item.PairingApproval,
                item.UpdateApproval,
                item.UpdatedAt,
            }));
        });
        application.MapGet("/api/v1/control/presence-enrollments", async (
            IServiceProvider services,
            CancellationToken token) =>
        {
            var control = services.GetService<ControlService>();
            if (control is null)
            {
                return Results.Json(Array.Empty<object>());
            }

            var names = new Dictionary<string, string?>(StringComparer.Ordinal);
            var data = services.GetService<LocalApiDataSource>();
            if (data is not null)
            {
                foreach (var peer in await data.ReadPeersAsync(token).ConfigureAwait(false))
                {
                    names[peer.PeerId] = peer.DisplayName;
                }
            }

            var all = await control.Store.ReadEnrollmentsAsync(token).ConfigureAwait(false);
            return Results.Json(all
                .Where(item => item.State == PresenceEnrollmentState.Pending)
                .Select(item => new
                {
                    item.EnrollmentId,
                    item.PeerId,
                    displayName = names.GetValueOrDefault(item.PeerId),
                    item.RequestedAt,
                    fingerprintSha256 = Convert.ToHexString(item.Fingerprint),
                }));
        });
        application.MapPost("/api/v1/control/grants/{peerId}", async (
            string peerId,
            HttpContext context,
            IAntiforgery antiforgery,
            IServiceProvider services,
            CancellationToken token) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var control = services.GetService<ControlService>();
            var body = await context.Request.ReadFromJsonAsync<ControlGrantInput>(token).ConfigureAwait(false);
            if (control is null || body is null)
            {
                return Results.BadRequest(new { error = "A grant body is required." });
            }

            if (body.MonitoringControls || body.PairingApproval || body.UpdateApproval)
            {
                var blocked = RequirePresence(
                    context,
                    LocalPresenceService.ControlGrantAction,
                    GrantSubject(peerId, body.MonitoringControls, body.PairingApproval, body.UpdateApproval));
                if (blocked is not null)
                {
                    return blocked;
                }
            }

            await control.SetGrantAsync(new ControlGrant(peerId, body.MonitoringControls, body.PairingApproval, null, body.UpdateApproval), token)
                .ConfigureAwait(false);
            return Results.Json(new { peerId, body.MonitoringControls, body.PairingApproval, body.UpdateApproval });
        });
        foreach (var (verb, approve) in new[] { ("approve", true), ("reject", false) })
        {
            application.MapPost("/api/v1/control/presence-enrollments/{enrollmentId}/" + verb, async (
                string enrollmentId,
                HttpContext context,
                IAntiforgery antiforgery,
                IServiceProvider services,
                CancellationToken token) =>
            {
                if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                if (approve)
                {
                    var blocked = RequirePresence(
                        context, LocalPresenceService.PresenceEnrollmentApproveAction, enrollmentId);
                    if (blocked is not null)
                    {
                        return blocked;
                    }
                }

                var control = services.GetService<ControlService>();
                var decided = control is null
                    ? null
                    : await control.DecideEnrollmentAsync(enrollmentId, approve, token).ConfigureAwait(false);
                return decided is null
                    ? Results.NotFound(new { error = "No pending enrollment with that id." })
                    : Results.Json(new { enrollmentId, state = decided.State.ToString() });
            });
        }
    }

    internal static string GrantSubject(string peerId, bool monitoringControls, bool pairingApproval, bool updateApproval = false) =>
        FormattableString.Invariant($"{peerId}|monitoring={monitoringControls}|pairing={pairingApproval}|update={updateApproval}");

    /// <summary>
    /// Enforces the desktop-local presence policy on a high-risk mutation; null means proceed. The refusal body carries a
    /// machine-readable <c>error</c> code (presence_required, presence_invalid, presence_not_enrolled) so the desktop
    /// client can obtain a challenge, have the person verified, and retry.
    /// </summary>
    private static IResult? RequirePresence(HttpContext context, string action, string subject)
    {
        if (context.RequestServices.GetService<LocalPresenceService>() is not { } presence)
        {
            return null;
        }

        var outcome = presence.Check(
            context.Request.Headers[LocalPresenceService.ChallengeHeader].ToString(),
            context.Request.Headers[LocalPresenceService.SignatureHeader].ToString(),
            action,
            subject);
        if (outcome == LocalPresenceOutcome.Satisfied)
        {
            return null;
        }

        var code = outcome switch
        {
            LocalPresenceOutcome.Required => "presence_required",
            LocalPresenceOutcome.NotEnrolled => "presence_not_enrolled",
            _ => "presence_invalid",
        };
        return Results.Json(new { error = code, action, subject }, statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Desktop-local user-presence (T045): status for the UI, one-shot challenges, and enrollment/removal of the
    /// presence key. The person is verified in the desktop client process, never here.
    /// </summary>
    private static void MapLocalPresenceEndpoints(WebApplication application)
    {
        application.MapGet("/api/v1/local-presence", (IServiceProvider services) =>
            services.GetService<LocalPresenceService>() is { } presence
                ? Results.Json(presence.GetStatus())
                : Results.Json(new LocalPresenceStatus(nameof(LocalPresencePolicy.Off), false, false, null, null, 0)));
        application.MapPost("/api/v1/local-presence/challenge", async (
            LocalPresenceChallengeRequest? body,
            HttpContext context,
            IAntiforgery antiforgery) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.RequestServices.GetService<LocalPresenceService>() is not { } presence)
            {
                return Results.NotFound();
            }

            var challenge = presence.CreateChallenge(body?.Action, body?.Subject);
            return challenge is null
                ? Results.BadRequest(new { error = "Unknown action, invalid subject, or too many pending challenges." })
                : Results.Json(new
                {
                    challenge.ChallengeId,
                    challenge.Nonce,
                    challenge.Action,
                    challenge.Subject,
                    expiresAtUnixSeconds = challenge.ExpiresAt.ToUnixTimeSeconds(),
                    payload = Convert.ToBase64String(challenge.Payload),
                });
        });
        application.MapPost("/api/v1/local-presence/enrollment", async (
            LocalPresenceProofRequest? body,
            HttpContext context,
            IAntiforgery antiforgery) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.RequestServices.GetService<LocalPresenceService>() is not { } presence)
            {
                return Results.NotFound();
            }

            return presence.TryEnroll(body?.PublicKeySpki, body?.ChallengeId, body?.Signature)
                ? Results.Json(presence.GetStatus())
                : Results.Json(
                    new { error = "Enrollment refused: a key is already enrolled, or the proof of possession is invalid." },
                    statusCode: StatusCodes.Status409Conflict);
        });
        application.MapPost("/api/v1/local-presence/removal", async (
            LocalPresenceProofRequest? body,
            HttpContext context,
            IAntiforgery antiforgery) =>
        {
            if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.RequestServices.GetService<LocalPresenceService>() is not { } presence)
            {
                return Results.NotFound();
            }

            return presence.TryRemove(body?.ChallengeId, body?.Signature)
                ? Results.Json(presence.GetStatus())
                : Results.Json(
                    new { error = "Removal refused: nothing is enrolled, or the proof from the enrolled key is invalid." },
                    statusCode: StatusCodes.Status403Forbidden);
        });
    }

    /// <summary>
    /// Server-Sent Events: each newly stored observation (local or replicated) as one JSON event, plus periodic
    /// comments so idle connections stay open. While connected, the viewer counts as live interest in this node's own
    /// data, which switches its collection to the fast sampling interval.
    /// </summary>
    private static async Task StreamLiveAsync(
        HttpContext context,
        PeerStore store,
        PeerHistoryQueryService history,
        LiveFeedOptions options,
        CancellationToken token)
    {
        var channel = Channel.CreateBounded<StoredOriginRecord>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        using var subscription = store.Live.Subscribe(record => channel.Writer.TryWrite(record));
        IDisposable? viewing = null;
        try
        {
            var local = await store.ReadLocalIdentityAsync(token).ConfigureAwait(false);
            if (local is not null && options.Interests is not null)
            {
                viewing = options.Interests.WatchLocal(local.PeerId);
            }

            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(": connected\n\n", token).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var record = await channel.Reader.ReadAsync(idle.Token).ConfigureAwait(false);
                    var json = JsonSerializer.Serialize(history.ToPoint(record, record.ReceivedAt), JsonSerializerOptions.Web);
                    await context.Response.WriteAsync(
                        $"id: {record.OriginPeerId}:{record.OriginStreamId}:{record.Sequence}\nevent: observation\ndata: {json}\n\n",
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": keepalive\n\n", token).ConfigureAwait(false);
                }

                await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The viewer disconnected or the host is stopping.
        }
        finally
        {
            viewing?.Dispose();
        }
    }

    private const int HistoryFeedCap = 5000;

    /// <summary>Parses the history feed range: ISO-8601 instants, to defaults to now and from to 7 days before it; from must not follow to.</summary>
    private static bool TryReadHistoryRange(
        string? from, string? to, IPeerClock clock, out DateTimeOffset start, out DateTimeOffset end, out string error)
    {
        error = string.Empty;
        start = end = default;
        var style = System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (to is null)
        {
            end = clock.UtcNow.ToUniversalTime();
        }
        else if (!DateTimeOffset.TryParse(to, culture, style, out end))
        {
            error = "to must be an ISO-8601 timestamp.";
            return false;
        }

        if (from is null)
        {
            start = end - TimeSpan.FromDays(7);
        }
        else if (!DateTimeOffset.TryParse(from, culture, style, out start))
        {
            error = "from must be an ISO-8601 timestamp.";
            return false;
        }

        if (start > end)
        {
            error = "from must not be after to.";
            return false;
        }

        if (end - start > TimeSpan.FromDays(366 * 5))
        {
            error = "The range may span at most five years.";
            return false;
        }

        return true;
    }

    private static async Task<IResult> QueryAsync<T>(Func<Task<T>> query)
    {
        try
        {
            return Results.Json(await query().ConfigureAwait(false));
        }
        catch (PeerQueryException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> HandlePhoneInvitationAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        PhonePairingService phonePairing,
        CancellationToken token)
    {
        if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(token).ConfigureAwait(false)
            : null;
        return await phonePairing.IssueAsync(form?["address"].ToString(), token).ConfigureAwait(false)
            ? Results.Redirect("/", permanent: false, preserveMethod: false)
            : Results.BadRequest(new { error = "Choose one of this node's advertisable private addresses." });
    }

    private static async Task<IResult> HandleInvitationAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        IPeerInvitationService invitationService,
        PeerInvitationStatusState statusState,
        CancellationToken token)
    {
        if (!await IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        PeerInvitationInput? input;
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(token).ConfigureAwait(false);
            _ = int.TryParse(form["port"], out var port);
            input = new PeerInvitationInput(
                form["expectedPeerId"].ToString(),
                form["address"].ToString(),
                port,
                form["expectedFingerprintSha256"].ToString(),
                form["securityPolicy"].ToString());
        }
        else
        {
            input = await context.Request.ReadFromJsonAsync<PeerInvitationInput>(token).ConfigureAwait(false);
        }

        if (!PeerInvitationValidator.TryValidate(input, out var command, out var error))
        {
            return Results.BadRequest(new { error });
        }

        var result = await invitationService.InviteAsync(command!, token).ConfigureAwait(false);
        statusState.Set(result);
        if (context.Request.HasFormContentType)
        {
            return Results.Redirect("/", permanent: false, preserveMethod: false);
        }

        return result.Status == PeerInvitationStatus.Unavailable
            ? Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Json(result);
    }

    private static async Task<IResult> RenderPageAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        LocalApiDataSource data,
        PeerInvitationStatusState invitationStatus,
        PhonePairingService phonePairing,
        PeerStore peerStore,
        IPeerClock clock,
        CancellationToken token)
    {
        var runtime = await data.ReadRuntimeAsync(token).ConfigureAwait(false);
        var peers = await data.ReadPeersAsync(token).ConfigureAwait(false);
        var pairings = await data.ReadPendingPairingsAsync(token).ConfigureAwait(false);
        var csrf = antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty;
        Func<string, string> encode = HtmlEncoder.Default.Encode;
        var nonce = (string)context.Items[CspNonceKey]!;
        var peerRows = string.Join(string.Empty, peers.Select(peer => $"""
            <tr><td>{encode(peer.DisplayName ?? peer.PeerId)}</td><td><code>{encode(peer.FingerprintSha256)}</code></td>
            <td>{encode(peer.TrustState)}</td><td>{(peer.TrustState == PeerTrustState.Active.ToString() ? MutationForm($"/api/peers/{Uri.EscapeDataString(peer.PeerId)}/revoke", "Revoke trust", csrf) : "Revoked")}</td></tr>
            """));
        var pairingRows = string.Join(string.Empty, pairings.Select(pairing => $"""
            <tr><td>{encode(pairing.DisplayName ?? pairing.RemotePeerId)}</td><td><code>{encode(pairing.FingerprintSha256)}</code></td>
            <td>{encode(pairing.ApprovalState)}</td><td>{MutationForm($"/api/pairings/{Uri.EscapeDataString(pairing.PairingId)}/approve", "Approve", csrf)} {MutationForm($"/api/pairings/{Uri.EscapeDataString(pairing.PairingId)}/reject", "Reject", csrf)}</td></tr>
            """));
        var localIdentity = runtime.LocalPeer is null
            ? "Not initialized"
            : $"{encode(runtime.LocalPeer.PeerId)} — <code>{encode(runtime.LocalPeer.FingerprintSha256)}</code>";
        var invitationResult = invitationStatus.LastResult is null
            ? "No invitation has been submitted in this process."
            : $"{encode(invitationStatus.LastResult.Status.ToString())}: {encode(invitationStatus.LastResult.Message)}";
        var adminSections = await LocalApiAdminSections.RenderAsync(
            context.RequestServices, peerStore, clock, HtmlEncoder.Default, token).ConfigureAwait(false);
        var html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Monitor App local administration</title><style nonce="{{nonce}}">
            body{font:1rem system-ui,sans-serif;max-width:72rem;margin:auto;padding:1rem;line-height:1.5}table{border-collapse:collapse;width:100%}th,td{border:1px solid #777;padding:.5rem;text-align:left}code{overflow-wrap:anywhere}form{display:inline}button{padding:.45rem .7rem}a:focus,button:focus,input:focus,select:focus{outline:3px solid #06c;outline-offset:2px}.status{padding:.75rem;background:#eef;color:#111}.scroll{overflow-x:auto}.sr{position:absolute;left:-10000px}fieldset{border:1px solid #777}.err{font-weight:bold}td input[type=number]{width:7rem}
            </style></head><body><header><h1>Monitor App local administration</h1><p class="status"><strong>Implemented now:</strong> local identity, peer trust, pairing decisions, current state, bounded history, and as-of reconstruction. <strong>Planned:</strong> the full experience, collectors, platform packages, and authenticated remote browser access.</p></header>
            <main data-csrf="{{encode(csrf)}}"><section aria-labelledby="runtime"><h2 id="runtime">Runtime</h2><p>Status: <strong>{{encode(runtime.Status)}}</strong></p><p>Local peer: {{localIdentity}}</p></section>
            <section aria-labelledby="pairings"><h2 id="pairings">Pending pairings</h2><table><thead><tr><th>Peer</th><th>Fingerprint</th><th>State</th><th>Decision</th></tr></thead><tbody>{{pairingRows}}</tbody></table></section>
            {{phonePairing.RenderSection(csrf, HtmlEncoder.Default)}}
            <section aria-labelledby="invite"><h2 id="invite">Invite a fresh peer</h2><p>Enter the identity shown by the other peer. Invitation transport must be configured by the running node.</p>
            <form method="post" action="/api/invitations"><input type="hidden" name="__RequestVerificationToken" value="{{encode(csrf)}}">
            <p><label for="expectedPeerId">Expected peer id</label><br><input id="expectedPeerId" name="expectedPeerId" required maxlength="256"></p>
            <p><label for="address">Explicit private or loopback IP address</label><br><input id="address" name="address" required inputmode="decimal"></p>
            <p><label for="port">Peer port</label><br><input id="port" name="port" required type="number" min="1" max="65535"></p>
            <p><label for="expectedFingerprintSha256">Expected SHA-256 fingerprint</label><br><input id="expectedFingerprintSha256" name="expectedFingerprintSha256" required minlength="64" maxlength="95"></p>
            <p><label for="securityPolicy">Declared security policy</label><br><select id="securityPolicy" name="securityPolicy"><option value="LoopbackPrototype">Loopback prototype</option><option value="AuthenticatedPrivateTransport">Authenticated private transport</option></select></p>
            <button type="submit">Dispatch invitation</button></form><p role="status"><strong>Last result:</strong> {{invitationResult}}</p></section>
            <section aria-labelledby="peers"><h2 id="peers">Trusted and revoked peers</h2><table><thead><tr><th>Peer</th><th>Fingerprint</th><th>Trust</th><th>Action</th></tr></thead><tbody>{{peerRows}}</tbody></table></section>
            {{adminSections}}
            {{LocalApiClientPage.Sections}}
            <section aria-labelledby="observations"><h2 id="observations">Raw record query</h2><p>Use the JSON endpoints for scripted access: <code>/api/v1/history?from=...&amp;to=...</code>, <code>/api/v1/state?asOf=...</code>, <code>/api/v1/catalog</code>, or page raw records with <code>/api/observations?originPeerId=...&amp;originStreamId=...</code>. Results identify origin, collection source, source time, received time, provenance, and quality.</p></section></main><script nonce="{{nonce}}">{{LocalApiClientPage.Script}}</script><script nonce="{{nonce}}">{{LocalApiAdminSections.Script}}</script></body></html>
            """;
        return Results.Content(html, MediaTypeNames.Text.Html);
    }

    private static string MutationForm(string action, string label, string csrf) => $"""
        <form method="post" action="{HtmlEncoder.Default.Encode(action)}"><input type="hidden" name="__RequestVerificationToken" value="{HtmlEncoder.Default.Encode(csrf)}"><button type="submit">{HtmlEncoder.Default.Encode(label)}</button></form>
        """;

    private static async Task<bool> IsValidMutationAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static bool TryParseUtc(string? text, out DateTimeOffset value)
    {
        value = default;
        if (!DateTimeOffset.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        value = parsed.ToUniversalTime();
        return true;
    }

    private static IResult MutationResult(HttpContext context, object value) =>
        context.Request.HasFormContentType
            ? Results.Redirect("/", permanent: false, preserveMethod: false)
            : Results.Json(value);
}

/// <summary>Pin a time window of raw observations against retention pruning.</summary>
internal sealed record PinIncidentRequest(string? Label, string? From, string? To);

internal sealed record PinnedIncidentView(string IncidentId, string Label, string From, string To, string CreatedAt)
{
    public static PinnedIncidentView Of(PinnedIncident incident) => new(
        incident.IncidentId,
        incident.Label,
        incident.From.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        incident.To.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        incident.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}
