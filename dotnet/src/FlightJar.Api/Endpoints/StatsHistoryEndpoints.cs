using FlightJar.Core.Stats;

namespace FlightJar.Api.Endpoints;

internal static class StatsHistoryEndpoints
{
    public static IEndpointRouteBuilder MapStatsHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/coverage", (PolarCoverage coverage) => Results.Json(coverage.SnapshotView()));
        app.MapPost("/api/coverage/reset", (PolarCoverage coverage) =>
        {
            coverage.Reset();
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/heatmap", (TrafficHeatmap hm) => Results.Json(hm.SnapshotView()));
        app.MapPost("/api/heatmap/reset", (TrafficHeatmap hm) =>
        {
            hm.Reset();
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/polar_heatmap", (PolarHeatmap ph) => Results.Json(ph.SnapshotView()));
        app.MapPost("/api/polar_heatmap/reset", (PolarHeatmap ph) =>
        {
            ph.Reset();
            return Results.Ok(new { ok = true });
        });

        return app;
    }
}
