using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlightJar.Api.Hosting;
using FlightJar.Core.State;

namespace FlightJar.Api.Endpoints;

/// <summary>
/// P2P federation endpoints.
/// <c>/p2p/ws</c> — WebSocket stream of sanitised snapshots (receiver lat/lon
/// and per-aircraft distance stripped). Useful for direct same-LAN peer
/// connections and for inspecting what this instance is sharing with the relay.
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
}
