using System.Text.Json;
using FlightJar.Api.Auth;
using FlightJar.Persistence.Watchlist;

namespace FlightJar.Api.Endpoints;

internal static class WatchlistEndpoints
{
    public static IEndpointRouteBuilder MapWatchlistEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/watchlist", (WatchlistStore store) =>
        {
            var snap = store.Snapshot();
            return Results.Json(new { icao24s = snap.Icao24s, last_seen = snap.LastSeen });
        }).RequireAuthSession();

        app.MapPost("/api/watchlist", async (HttpContext ctx, WatchlistStore store) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("icao24s", out var a)
                ? a
                : root;
            if (arr.ValueKind != JsonValueKind.Array)
            {
                return Results.BadRequest(new { error = "expected array or {icao24s: array}" });
            }
            if (arr.GetArrayLength() > 10_000)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            var incoming = new List<string>();
            foreach (var v in arr.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.String && v.GetString() is string s)
                {
                    incoming.Add(s);
                }
            }
            var snap = store.Replace(incoming);
            return Results.Json(new { icao24s = snap.Icao24s, last_seen = snap.LastSeen });
        }).RequireAuthSession();

        return app;
    }
}
