using FlightJar.Api.Hosting;
using FlightJar.Api.Hosting.Blackspots;

namespace FlightJar.Api.Endpoints;

internal static class BlackspotsEndpoints
{
    public static IEndpointRouteBuilder MapBlackspotsEndpoints(this IEndpointRouteBuilder app)
    {
        // Target altitude comes from the frontend slider; unset means "use the
        // default the worker prewarmed at startup" (FL100 / 3048 m MSL). Bounds
        // are wide enough for anything from surface GA to high-level airliners.
        app.MapGet("/api/blackspots", async (
            BlackspotsWorker worker, double? target_alt_m, CancellationToken ct) =>
        {
            if (!worker.Enabled)
            {
                return Results.Json(new
                {
                    enabled = false,
                    cells = Array.Empty<object>(),
                    blockers = Array.Empty<object>(),
                    blocker_grid_deg = 0.0,
                });
            }
            var alt = target_alt_m ?? BlackspotsWorker.DefaultTargetAltitudeM;
            // 0 is a sentinel for "ground level at each cell" — the grid uses
            // sampled DEM elevation + a 2 m fuselage offset per cell instead of
            // a fixed MSL value. Anything positive is treated as absolute MSL.
            if (alt < 0 || alt > 20_000)
            {
                return Results.BadRequest(new { error = "target_alt_m out of range [0, 20000]" });
            }
            try
            {
                var grid = await worker.GetOrComputeAsync(alt, ct);
                if (grid is null)
                {
                    return Results.Json(new
                    {
                        enabled = true,
                        computing = true,
                        cells = Array.Empty<object>(),
                        blockers = Array.Empty<object>(),
                        blocker_grid_deg = 0.0,
                    });
                }
                return Results.Json(grid.SnapshotView());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty;
            }
        });

        app.MapPost("/api/blackspots/recompute", (BlackspotsWorker worker) =>
        {
            if (!worker.Enabled)
            {
                return Results.Json(new { enabled = false });
            }
            worker.TriggerRecompute();
            return Results.Ok(new { ok = true });
        });

        // Hillshaded blocking-face raster — single RGBA PNG covering the
        // receiver's bbox. Sea/no-data pixels are transparent; land pixels show
        // greyscale relief (Lambertian hillshade); pixels above the LOS line to
        // the target altitude AND visible from the antenna are tinted red in
        // proportion to how steeply they rise above the LOS plane. Returns
        // base64'd inside JSON so the frontend can drop it straight into a
        // Leaflet image overlay.
        app.MapGet("/api/blackspots/faces", async (
            BlackspotsWorker worker, double? target_alt_m, CancellationToken ct) =>
        {
            if (!worker.Enabled)
            {
                return Results.Json(new { enabled = false });
            }
            var alt = target_alt_m ?? BlackspotsWorker.DefaultTargetAltitudeM;
            if (alt < 0 || alt > 20_000)
            {
                return Results.BadRequest(new { error = "target_alt_m out of range [0, 20000]" });
            }
            try
            {
                var raster = await worker.GetOrComputeFaceAsync(alt, ct);
                if (raster is null)
                {
                    return Results.Json(new { enabled = true, computing = true });
                }
                var png = FlightJar.Core.Imaging.PngWriter.EncodeRgba(
                    raster.Width, raster.Height, raster.Rgba);
                return Results.Json(new
                {
                    enabled = true,
                    bounds = new
                    {
                        min_lat = raster.MinLat,
                        max_lat = raster.MaxLat,
                        min_lon = raster.MinLon,
                        max_lon = raster.MaxLon,
                    },
                    width = raster.Width,
                    height = raster.Height,
                    grid_deg = raster.Params.GridDeg,
                    png_base64 = Convert.ToBase64String(png),
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty;
            }
        });

        // Live progress poll for the currently-running compute at a given altitude.
        // Designed to be hit every ~150 ms by the frontend while awaiting a fresh
        // grid; returns {active: false} when the altitude is cached / queued /
        // disabled so the caller can stop polling. `phase` lets the UI label the
        // step (loading_terrain → computing_grid → computing_face) so the bar
        // stays informative across the whole pipeline rather than going blank
        // during preload or face render.
        app.MapGet("/api/blackspots/progress", (BlackspotsWorker worker, double? target_alt_m) =>
        {
            if (!worker.Enabled)
            {
                return Results.Json(new
                {
                    active = false,
                    progress = 0.0,
                    phase = BlackspotsProgressPhase.Idle,
                });
            }
            var alt = target_alt_m ?? BlackspotsWorker.DefaultTargetAltitudeM;
            var snap = worker.GetProgress(alt);
            return Results.Json(new
            {
                active = snap.Active,
                progress = snap.Fraction,
                phase = snap.Phase,
            });
        });

        return app;
    }
}
