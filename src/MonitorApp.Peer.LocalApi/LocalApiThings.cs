using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MonitorApp.Model;
using MonitorApp.Planning.Model;

namespace MonitorApp.Peer.LocalApi;

/// <summary>Everything the Things screen shows, plus the kinds a person can pick when adding one.</summary>
public sealed record ThingsResponse(
    IReadOnlyList<ThingSummary> Things, IReadOnlyList<ThingKindChoice> Kinds, IReadOnlyList<ThingLinkType>? LinkTypes = null,
    IReadOnlyList<ConnectorChoice>? Connectors = null, IReadOnlyList<ThingWiring>? Wiring = null, IReadOnlyList<MissingNeed>? Missing = null, IReadOnlyList<RackDrawing>? Racks = null);

internal sealed record ThingCaptureRequest(string? Text, string? KindId);

internal sealed record ThingEditRequest(
    string? Id, string? Name, string? KindId, string? Note, string? InsideId,
    string? Lifecycle = null, IReadOnlyList<string>? Tags = null, IReadOnlyList<ThingValueEdit>? Values = null,
    IReadOnlyList<ThingLinkEdit>? AddLinks = null, IReadOnlyList<string>? RemoveLinks = null,
    IReadOnlyList<ThingPortEdit>? AddPorts = null, IReadOnlyList<string>? RemovePorts = null,
    IReadOnlyList<ThingConnectEdit>? Connect = null, IReadOnlyList<string>? Disconnect = null, string? Slot = null);

/// <summary>
/// The shared model over the loopback API (T073, T074). Reading is open to the local app like every other read; adding a
/// thing is a local mutation and needs the antiforgery token. It records only what the person typed, marked as entered by them.
/// </summary>
internal static class LocalApiThings
{
    public static void Map(WebApplication application)
    {
        // ?asOf=<ISO 8601> shows the model as it was then (time travel over things, containment and values).
        application.MapGet("/api/v1/things", async (HttpContext context, string? asOf, CancellationToken token) =>
        {
            if (context.RequestServices.GetService<ModelStore>() is not { } store)
            {
                return Results.NotFound(new { error = "The shared model is not available on this node." });
            }

            DateTimeOffset? at = null;
            if (!string.IsNullOrWhiteSpace(asOf))
            {
                if (!DateTimeOffset.TryParse(asOf, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    return Results.BadRequest(new { error = "asOf must be an ISO 8601 time." });
                }

                at = parsed;
            }

            var model = await store.LoadAsync(at, cancellationToken: token).ConfigureAwait(false);
            return Results.Json(new ThingsResponse(
                Things.List(model), Things.KindChoices(store.Library), Things.LinkTypes, ThingsWiring.Connectors(), ThingsWiring.For(model),
                at is null ? ThingsWiring.Missing(model) : [], ThingsWiring.Racks(model)));
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

        // Thing ids hold ':' and '/' ("peer:abc/memory"), so the id travels in the body rather than the path.
        application.MapPost("/api/v1/things/edit", async (HttpContext context, IAntiforgery antiforgery, CancellationToken token) =>
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
                return Results.BadRequest(new { error = "Send the change as JSON." });
            }

            var request = await context.Request.ReadFromJsonAsync<ThingEditRequest>(token).ConfigureAwait(false);
            try
            {
                var thing = await Things.EditAsync(store, request?.Id ?? string.Empty, new ThingEdit(
                    request?.Name, request?.KindId, request?.Note, request?.InsideId, request?.Lifecycle, request?.Tags, request?.Values, request?.AddLinks, request?.RemoveLinks,
                    request?.AddPorts, request?.RemovePorts, request?.Connect, request?.Disconnect, request?.Slot), ThingsWiring.IsKnownConnector, token).ConfigureAwait(false);
                return Results.Json(thing);
            }
            catch (ModelValidationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
    }
}
