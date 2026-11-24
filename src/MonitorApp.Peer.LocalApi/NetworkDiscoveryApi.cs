using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MonitorApp.Peer.LocalApi;

/// <summary>
/// This computer's "Find devices on this network" switch (owner decision 2026-11-24: one switch per computer, off until
/// turned on, own subnet only) and what the last pass found.
/// </summary>
public sealed record NetworkDiscoveryStatus(
    bool Enabled,
    bool Running,
    DateTimeOffset? LastPassAt,
    int DevicesFound,
    int DevicesAnswered,
    IReadOnlyList<string> Networks,
    string? Problem,
    string Explanation);

/// <summary>Implemented by the node, which owns the saved choice and the discovery schedule.</summary>
public interface INetworkDiscoveryController
{
    NetworkDiscoveryStatus GetStatus();

    /// <summary>Saves the choice; turning it on starts a pass straight away.</summary>
    NetworkDiscoveryStatus SetEnabled(bool enabled);
}

internal sealed record NetworkDiscoveryRequest(bool Enabled);

internal static class NetworkDiscoveryEndpoints
{
    public static void Map(WebApplication application, Func<HttpContext, IAntiforgery, Task<bool>> isValidMutation)
    {
        application.MapGet("/api/v1/network/discovery", (HttpContext context) =>
            context.RequestServices.GetService<INetworkDiscoveryController>() is { } discovery ? Results.Json(discovery.GetStatus()) : Results.NotFound());
        application.MapPost("/api/v1/network/discovery", async (HttpContext context, IAntiforgery antiforgery, CancellationToken token) =>
        {
            if (!await isValidMutation(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.RequestServices.GetService<INetworkDiscoveryController>() is not { } discovery)
            {
                return Results.NotFound();
            }

            var request = context.Request.HasJsonContentType()
                ? await context.Request.ReadFromJsonAsync<NetworkDiscoveryRequest>(token).ConfigureAwait(false)
                : null;
            return request is null
                ? Results.BadRequest(new { error = "Say whether this computer should look for devices on its network." })
                : Results.Json(discovery.SetEnabled(request.Enabled));
        });
    }
}
