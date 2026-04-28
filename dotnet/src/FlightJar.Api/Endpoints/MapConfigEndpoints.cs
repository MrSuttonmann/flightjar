using FlightJar.Clients.Vfrmap;
using FlightJar.Core.Configuration;
using FlightJar.Core.ReferenceData;

namespace FlightJar.Api.Endpoints;

internal static class MapConfigEndpoints
{
    public static IEndpointRouteBuilder MapMapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        // `layer_status` reports each gated map layer's `{enabled, reason}` so
        // the frontend can render disabled rows in the layers control with an
        // info icon explaining why and how to enable them — instead of silently
        // hiding overlays whose backing config is missing.
        app.MapGet("/api/map_config", (AppOptions opts, VfrmapCycle vfrmap) =>
        {
            var openAipKey = opts.OpenAipApiKey;
            var vfrmapDate = vfrmap.CurrentDate ?? opts.VfrmapChartDate;
            var openAipEnabled = !string.IsNullOrWhiteSpace(openAipKey);
            var vfrmapEnabled = !string.IsNullOrWhiteSpace(vfrmapDate);
            var blackspotsEnabled = opts.BlackspotsEnabled
                && opts.LatRef is not null && opts.LonRef is not null;

            string? blackspotsReason = blackspotsEnabled ? null
                : !opts.BlackspotsEnabled
                    ? "Set BLACKSPOTS_ENABLED=1 to enable."
                    : "Set LAT_REF and LON_REF to enable.";

            return Results.Json(new
            {
                openaip_api_key = openAipKey,
                vfrmap_chart_date = vfrmapDate,
                layer_status = new
                {
                    openaip = new
                    {
                        enabled = openAipEnabled,
                        reason = openAipEnabled ? null
                            : "Set OPENAIP_API_KEY to enable. Free key at openaip.net.",
                    },
                    vfrmap = new
                    {
                        enabled = vfrmapEnabled,
                        reason = vfrmapEnabled ? null
                            : "Set VFRMAP_CHART_DATE, or check internet for auto-discovery.",
                    },
                    blackspots = new
                    {
                        enabled = blackspotsEnabled,
                        reason = blackspotsReason,
                    },
                },
            });
        });

        app.MapGet("/api/airports", (
            [Microsoft.AspNetCore.Mvc.FromServices] AirportsDb db,
            double? min_lat, double? max_lat, double? min_lon, double? max_lon, int? limit) =>
        {
            if (ValidateBbox(min_lat, max_lat, min_lon, max_lon) is { } err) return err;
            var hits = db.Bbox(
                min_lat!.Value, min_lon!.Value, max_lat!.Value, max_lon!.Value,
                limit: Math.Clamp(limit ?? 2000, 1, 5000));
            return Results.Json(hits);
        });

        app.MapGet("/api/navaids", (
            [Microsoft.AspNetCore.Mvc.FromServices] NavaidsDb db,
            double? min_lat, double? max_lat, double? min_lon, double? max_lon, int? limit) =>
        {
            if (ValidateBbox(min_lat, max_lat, min_lon, max_lon) is { } err) return err;
            var hits = db.Bbox(
                min_lat!.Value, min_lon!.Value, max_lat!.Value, max_lon!.Value,
                limit: Math.Clamp(limit ?? 2000, 1, 5000));
            return Results.Json(hits);
        });

        return app;
    }

    private static IResult? ValidateBbox(
        double? minLat, double? maxLat, double? minLon, double? maxLon)
    {
        if (minLat is not double mnLat || maxLat is not double mxLat
            || minLon is not double mnLon || maxLon is not double mxLon)
        {
            return Results.BadRequest(new { error = "min_lat/max_lat/min_lon/max_lon required" });
        }
        if (mnLat < -90 || mnLat > 90 || mxLat < -90 || mxLat > 90
            || mnLon < -180 || mnLon > 180 || mxLon < -180 || mxLon > 180)
        {
            return Results.BadRequest(new { error = "lat/lon out of range" });
        }
        return null;
    }
}
