using System.Net;
using System.Net.Mime;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.LocalApi;

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
        IPeerInvitationService? invitationService = null)
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
            PeerHistoryQueryService history,
            CancellationToken token) => QueryAsync(() => history.QueryHistoryAsync(
                from, to, subjectEntityId, signalId, originPeerId, limit, cursor, token)));
        application.MapGet("/api/v1/state", (
            string? asOf,
            int? lookbackSeconds,
            string? subjectEntityId,
            string? signalId,
            int? limit,
            PeerHistoryQueryService history,
            CancellationToken token) => QueryAsync(() => history.ReconstructAsync(
                asOf, lookbackSeconds, subjectEntityId, signalId, limit, token)));

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
        application.MapPost("/api/invitations", HandleInvitationAsync);
        application.MapGet("/", RenderPageAsync);
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
