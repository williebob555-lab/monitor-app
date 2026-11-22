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

internal sealed record LiveFeedOptions(LiveInterestRegistry? Interests);

internal sealed record ControlGrantInput(bool MonitoringControls, bool PairingApproval);

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
        MonitoringControlState? monitoring = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(peerStore);
        ArgumentNullException.ThrowIfNull(pairingStore);
        ArgumentNullException.ThrowIfNull(pairingCoordinator);
        ArgumentNullException.ThrowIfNull(clock);
        options.Validate();

        var builder = WebApplication.CreateSlimBuilder();
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
        await application.DisposeAsync().ConfigureAwait(false);
    }

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
                }),
            });
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
                alertMutes = mutes.Select(item => new { item.Scope, item.Until }),
            });
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

            await control.SetGrantAsync(new ControlGrant(peerId, body.MonitoringControls, body.PairingApproval, null), token)
                .ConfigureAwait(false);
            return Results.Json(new { peerId, body.MonitoringControls, body.PairingApproval });
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
        var html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Monitor App local administration</title><style nonce="{{nonce}}">
            body{font:1rem system-ui,sans-serif;max-width:72rem;margin:auto;padding:1rem;line-height:1.5}table{border-collapse:collapse;width:100%}th,td{border:1px solid #777;padding:.5rem;text-align:left}code{overflow-wrap:anywhere}form{display:inline}button{padding:.45rem .7rem}a:focus,button:focus,input:focus,select:focus{outline:3px solid #06c;outline-offset:2px}.status{padding:.75rem;background:#eef;color:#111}.scroll{overflow-x:auto}.sr{position:absolute;left:-10000px}fieldset{border:1px solid #777}
            </style></head><body><header><h1>Monitor App local administration</h1><p class="status"><strong>Implemented now:</strong> local identity, peer trust, pairing decisions, current state, bounded history, and as-of reconstruction. <strong>Planned:</strong> the full experience, collectors, platform packages, and authenticated remote browser access.</p></header>
            <main><section aria-labelledby="runtime"><h2 id="runtime">Runtime</h2><p>Status: <strong>{{encode(runtime.Status)}}</strong></p><p>Local peer: {{localIdentity}}</p></section>
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
            {{LocalApiClientPage.Sections}}
            <section aria-labelledby="observations"><h2 id="observations">Raw record query</h2><p>Use the JSON endpoints for scripted access: <code>/api/v1/history?from=...&amp;to=...</code>, <code>/api/v1/state?asOf=...</code>, <code>/api/v1/catalog</code>, or page raw records with <code>/api/observations?originPeerId=...&amp;originStreamId=...</code>. Results identify origin, collection source, source time, received time, provenance, and quality.</p></section></main><script nonce="{{nonce}}">{{LocalApiClientPage.Script}}</script></body></html>
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

    private static IResult MutationResult(HttpContext context, object value) =>
        context.Request.HasFormContentType
            ? Results.Redirect("/", permanent: false, preserveMethod: false)
            : Results.Json(value);
}
