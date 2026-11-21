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
        IPeerClock clock)
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
        builder.Services.AddSingleton(new LocalApiDataSource(options.DatabasePath, peerStore));

        var application = builder.Build();
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
        application.MapGet("/", RenderPageAsync);
    }

    private static async Task<IResult> RenderPageAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        LocalApiDataSource data,
        CancellationToken token)
    {
        var runtime = await data.ReadRuntimeAsync(token).ConfigureAwait(false);
        var peers = await data.ReadPeersAsync(token).ConfigureAwait(false);
        var pairings = await data.ReadPendingPairingsAsync(token).ConfigureAwait(false);
        var csrf = antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty;
        Func<string, string> encode = HtmlEncoder.Default.Encode;
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
        var html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Monitor App local administration</title><style>
            body{font:1rem system-ui,sans-serif;max-width:72rem;margin:auto;padding:1rem;line-height:1.5}table{border-collapse:collapse;width:100%}th,td{border:1px solid #777;padding:.5rem;text-align:left}code{overflow-wrap:anywhere}form{display:inline}button{padding:.45rem .7rem}a:focus,button:focus{outline:3px solid #06c;outline-offset:2px}.status{padding:.75rem;background:#eef}
            </style></head><body><header><h1>Monitor App local administration</h1><p class="status"><strong>Implemented now:</strong> local identity, peer trust, pairing decisions, and stored observation query. <strong>Planned:</strong> the full experience, collectors, platform packages, and authenticated remote browser access.</p></header>
            <main><section aria-labelledby="runtime"><h2 id="runtime">Runtime</h2><p>Status: <strong>{{encode(runtime.Status)}}</strong></p><p>Local peer: {{localIdentity}}</p></section>
            <section aria-labelledby="pairings"><h2 id="pairings">Pending pairings</h2><table><thead><tr><th>Peer</th><th>Fingerprint</th><th>State</th><th>Decision</th></tr></thead><tbody>{{pairingRows}}</tbody></table></section>
            <section aria-labelledby="peers"><h2 id="peers">Trusted and revoked peers</h2><table><thead><tr><th>Peer</th><th>Fingerprint</th><th>Trust</th><th>Action</th></tr></thead><tbody>{{peerRows}}</tbody></table></section>
            <section aria-labelledby="observations"><h2 id="observations">Stored observations</h2><p>Use the JSON query endpoint with an origin peer and stream: <code>/api/observations?originPeerId=...&amp;originStreamId=...</code>. Results identify origin, collection source, source time, received time, provenance, and quality.</p></section></main></body></html>
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
