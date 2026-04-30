using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlightJar.Api.Auth;
using FlightJar.Api.Hosting;
using FlightJar.Core.State;
using FlightJar.Persistence.P2P;

namespace FlightJar.Api.Endpoints;

/// <summary>
/// P2P federation endpoints.
/// <list type="bullet">
/// <item><c>GET/POST /api/p2p/config</c> — UI-toggleable on/off and
/// share-site-name. Default: enabled.</item>
/// <item><c>/p2p/ws</c> — WebSocket stream of sanitised snapshots (receiver
/// lat/lon and per-aircraft distance stripped). Useful for direct same-LAN
/// peer connections and for inspecting what this instance is sharing.</item>
/// </list>
/// </summary>
internal static class P2PEndpoints
{
    private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static IEndpointRouteBuilder MapP2PEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/p2p/config", (P2PConfigStore store) =>
        {
            var cfg = store.Current;
            return Results.Json(new
            {
                version = P2PConfigStore.SchemaVersion,
                enabled = cfg.Enabled,
                share_site_name = cfg.ShareSiteName,
            });
        }).RequireAuthSession();

        app.MapPost("/api/p2p/config", async (HttpContext ctx, P2PConfigStore store) =>
        {
            P2PConfigPayload? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<P2PConfigPayload>(
                    ctx.Request.Body, _jsonOpts, ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "expected {enabled?: bool, share_site_name?: bool}" });
            }
            if (payload is null)
            {
                return Results.BadRequest(new { error = "empty body" });
            }
            var current = store.Current;
            var updated = store.Replace(new P2PConfig
            {
                Enabled = payload.Enabled ?? current.Enabled,
                ShareSiteName = payload.ShareSiteName ?? current.ShareSiteName,
            });
            return Results.Json(new
            {
                enabled = updated.Enabled,
                share_site_name = updated.ShareSiteName,
            });
        }).RequireAuthSession();

        app.Map("/p2p/ws", async (
            HttpContext ctx,
            SnapshotBroadcaster broadcaster,
            CurrentSnapshot current,
            ILogger<Program> log) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var sub = broadcaster.Subscribe();
            try
            {
                // Send current snapshot immediately (sanitised)
                var initial = Sanitise(current.Snapshot);
                await SendAsync(socket, initial, ctx.RequestAborted);

                // Then stream every subsequent broadcast, sanitised on the fly
                await foreach (var payload in sub.ReadAllAsync(ctx.RequestAborted))
                {
                    var snap = JsonSerializer.Deserialize<RegistrySnapshot>(payload, _jsonOpts);
                    if (snap is not null)
                    {
                        await SendAsync(socket, Sanitise(snap), ctx.RequestAborted);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                log.LogDebug(ex, "p2p/ws client disconnected");
            }
            finally
            {
                broadcaster.Unsubscribe(sub.Id);
            }
            return Results.Empty;
        });

        return app;
    }

    private static string Sanitise(RegistrySnapshot snap)
    {
        var aircraft = new List<SnapshotAircraft>(snap.Aircraft.Count);
        foreach (var ac in snap.Aircraft)
        {
            if (ac.Peer == true) continue;
            aircraft.Add(ac with { DistanceKm = null });
        }
        var sanitised = snap with
        {
            Receiver = null,
            SiteName = null,
            Aircraft = aircraft,
            Count = aircraft.Count,
            Positioned = aircraft.Count(a => a.Lat.HasValue),
        };
        return JsonSerializer.Serialize(sanitised, _jsonOpts);
    }

    private static async Task SendAsync(WebSocket socket, string payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private sealed record P2PConfigPayload(bool? Enabled, bool? ShareSiteName);
}
