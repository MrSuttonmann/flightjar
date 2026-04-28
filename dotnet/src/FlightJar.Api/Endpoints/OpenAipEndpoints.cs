using FlightJar.Clients.OpenAip;

namespace FlightJar.Api.Endpoints;

internal static class OpenAipEndpoints
{
    public static IEndpointRouteBuilder MapOpenAipEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/openaip/airspaces", (
            [Microsoft.AspNetCore.Mvc.FromServices] OpenAipClient client,
            [Microsoft.AspNetCore.Mvc.FromServices] ILogger<OpenAipClient> logger,
            double? min_lat, double? max_lat, double? min_lon, double? max_lon,
            CancellationToken ct) =>
            ServeOpenAip<Airspace>(client.GetAirspacesAsync, "airspaces", logger, min_lat, max_lat, min_lon, max_lon, ct));

        app.MapGet("/api/openaip/obstacles", (
            [Microsoft.AspNetCore.Mvc.FromServices] OpenAipClient client,
            [Microsoft.AspNetCore.Mvc.FromServices] ILogger<OpenAipClient> logger,
            double? min_lat, double? max_lat, double? min_lon, double? max_lon,
            CancellationToken ct) =>
            ServeOpenAip<Obstacle>(client.GetObstaclesAsync, "obstacles", logger, min_lat, max_lat, min_lon, max_lon, ct));

        app.MapGet("/api/openaip/reporting_points", (
            [Microsoft.AspNetCore.Mvc.FromServices] OpenAipClient client,
            [Microsoft.AspNetCore.Mvc.FromServices] ILogger<OpenAipClient> logger,
            double? min_lat, double? max_lat, double? min_lon, double? max_lon,
            CancellationToken ct) =>
            ServeOpenAip<ReportingPoint>(client.GetReportingPointsAsync, "reporting_points", logger, min_lat, max_lat, min_lon, max_lon, ct));

        return app;
    }

    // OpenAIP bbox overlays — backed by OpenAipClient's disk cache. The frontend
    // fires one request per moveend and aborts the previous one mid-flight when
    // the user pans, which trips the request's cancellation token and bubbles an
    // OperationCanceledException out of the throttle's semaphore wait. That's
    // expected — swallow it so it doesn't spam Kestrel's error log.
    private static (double mnLat, double mxLat, double mnLon, double mxLon)? ReadBbox(
        double? minLat, double? maxLat, double? minLon, double? maxLon)
    {
        if (minLat is not double mnLat || maxLat is not double mxLat
            || minLon is not double mnLon || maxLon is not double mxLon) return null;
        if (mnLat < -90 || mnLat > 90 || mxLat < -90 || mxLat > 90
            || mnLon < -180 || mnLon > 180 || mxLon < -180 || mxLon > 180) return null;
        return (mnLat, mxLat, mnLon, mxLon);
    }

    private static async Task<IResult> ServeOpenAip<T>(
        Func<double, double, double, double, CancellationToken, Task<IReadOnlyList<T>>> fetch,
        string endpoint,
        ILogger logger,
        double? min_lat, double? max_lat, double? min_lon, double? max_lon,
        CancellationToken ct)
    {
        var bbox = ReadBbox(min_lat, max_lat, min_lon, max_lon);
        if (bbox is null)
        {
            logger.LogInformation(
                "openaip /api/openaip/{Endpoint} rejected: bbox missing or out of range",
                endpoint);
            return Results.BadRequest(new { error = "min_lat/max_lat/min_lon/max_lon required, in range" });
        }
        var (mnLat, mxLat, mnLon, mxLon) = bbox.Value;
        try
        {
            var items = await fetch(mnLat, mnLon, mxLat, mxLon, ct);
            logger.LogInformation(
                "openaip /api/openaip/{Endpoint} bbox=({MnLat:0.###},{MnLon:0.###})-({MxLat:0.###},{MxLon:0.###}) → {Count} items",
                endpoint, mnLat, mnLon, mxLat, mxLon, items.Count);
            return Results.Json(items);
        }
        catch (OperationCanceledException)
        {
            // OCE here covers two cases. The usual one is our own `ct` firing
            // — the map pan aborted this request, Kestrel won't be able to
            // write the response anyway. The less-obvious one: OpenAipClient
            // shares an in-flight fetch across concurrent callers via
            // `inflight.GetOrAdd`, so if a *sibling* request's ct cancelled
            // the fetch task, we inherit its OCE even though our own ct is
            // fine. In both cases the client will re-request on the next
            // moveend, so returning Empty is the right recovery — and it
            // keeps Kestrel's error log from filling with benign noise.
            return Results.Empty;
        }
    }
}
