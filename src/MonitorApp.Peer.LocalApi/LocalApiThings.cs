using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Model;

namespace MonitorApp.Peer.LocalApi;

/// <summary>Everything the Things screen shows, plus the kinds a person can pick when adding one.</summary>
public sealed record ThingsResponse(IReadOnlyList<ThingSummary> Things, IReadOnlyList<ThingKindChoice> Kinds);

internal sealed record ThingCaptureRequest(string? Text, string? KindId);

/// <summary>
/// The shared model over the loopback API (T073, T074). Reading is open to the local app like every other read; adding a
/// thing is a local mutation and needs the antiforgery token. It records only what the person typed, marked as entered by them.
/// </summary>
internal static class LocalApiThings
{
    public static void Map(WebApplication application)
    {
        application.MapGet("/api/v1/things", async (HttpContext context, CancellationToken token) =>
        {
            if (context.RequestServices.GetService<ModelStore>() is not { } store)
            {
                return Results.NotFound(new { error = "The shared model is not available on this node." });
            }

            var model = await store.LoadAsync(cancellationToken: token).ConfigureAwait(false);
            return Results.Json(new ThingsResponse(Things.List(model), Things.KindChoices(store.Library)));
        });

        application.MapPost("/api/v1/things", async (HttpContext context, IAntiforgery antiforgery, CancellationToken token) =>
        {
            if (!await LocalApiHost.IsValidMutationAsync(context, antiforgery).ConfigureAwait(false))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (context.RequestServices.GetService<ModelStore>() is not { } store)
            {
                return Results.NotFound(new { error = "The shared model is not available on this node." });
            }

            if (!context.Request.HasJsonContentType())
            {
                return Results.BadRequest(new { error = "Send the new thing as JSON." });
            }

            var request = await context.Request.ReadFromJsonAsync<ThingCaptureRequest>(token).ConfigureAwait(false);
            try
            {
                var thing = await Things.CaptureAsync(store, request?.Text ?? string.Empty, request?.KindId, token).ConfigureAwait(false);
                return Results.Json(thing, statusCode: StatusCodes.Status201Created);
            }
            catch (ModelValidationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
    }
}
