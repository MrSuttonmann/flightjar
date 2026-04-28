using System.Text;
using FlightJar.Api.Hosting;
using FlightJar.Core;
using FlightJar.Core.Configuration;

namespace FlightJar.Api.Endpoints;

internal static class StatsEndpoints
{
    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", (IBeastConnectionState state) =>
            state.IsConnected
                ? Results.Ok(new { status = "ok" })
                : Results.Json(new { status = "disconnected" }, statusCode: StatusCodes.Status503ServiceUnavailable));

        app.MapGet("/api/aircraft", (CurrentSnapshot current) =>
            Results.Content(current.Json, "application/json; charset=utf-8"));

        app.MapGet("/api/stats", (
            [Microsoft.AspNetCore.Mvc.FromServices] AppOptions opts,
            [Microsoft.AspNetCore.Mvc.FromServices] IBeastConnectionState state,
            [Microsoft.AspNetCore.Mvc.FromServices] IBeastFrameStats frameStats,
            [Microsoft.AspNetCore.Mvc.FromServices] SnapshotBroadcaster broadcaster,
            [Microsoft.AspNetCore.Mvc.FromServices] CurrentSnapshot current) =>
            Results.Json(new
            {
                site_name = opts.SiteName,
                beast_host = opts.BeastHost,
                beast_port = opts.BeastPort,
                beast_target = $"{opts.BeastHost}:{opts.BeastPort}",
                beast_connected = state.IsConnected,
                frames = frameStats.FrameCount,
                websocket_clients = broadcaster.SubscriberCount,
                aircraft = current.Snapshot.Count,
                positioned = current.Snapshot.Positioned,
                uptime_s = (int)(DateTime.UtcNow - Program.StartedAt).TotalSeconds,
                version = Environment.GetEnvironmentVariable("FLIGHTJAR_VERSION") ?? "dev",
            }));

        app.MapGet("/metrics", (
            [Microsoft.AspNetCore.Mvc.FromServices] IBeastConnectionState state,
            [Microsoft.AspNetCore.Mvc.FromServices] IBeastFrameStats frameStats,
            [Microsoft.AspNetCore.Mvc.FromServices] SnapshotBroadcaster broadcaster,
            [Microsoft.AspNetCore.Mvc.FromServices] CurrentSnapshot current) =>
        {
            var sb = new StringBuilder();
            sb.Append("# HELP flightjar_beast_connected 1 if the BEAST feed is currently connected\n");
            sb.Append("# TYPE flightjar_beast_connected gauge\n");
            sb.Append("flightjar_beast_connected ").Append(state.IsConnected ? 1 : 0).Append('\n');
            sb.Append("# HELP flightjar_frames_total BEAST frames ingested since startup\n");
            sb.Append("# TYPE flightjar_frames_total counter\n");
            sb.Append("flightjar_frames_total ").Append(frameStats.FrameCount).Append('\n');
            sb.Append("# HELP flightjar_aircraft Tracked aircraft in the current snapshot\n");
            sb.Append("# TYPE flightjar_aircraft gauge\n");
            sb.Append("flightjar_aircraft ").Append(current.Snapshot.Count).Append('\n');
            sb.Append("# HELP flightjar_ws_clients Connected WebSocket clients\n");
            sb.Append("# TYPE flightjar_ws_clients gauge\n");
            sb.Append("flightjar_ws_clients ").Append(broadcaster.SubscriberCount).Append('\n');
            return Results.Text(sb.ToString(), "text/plain; version=0.0.4");
        });

        return app;
    }
}
