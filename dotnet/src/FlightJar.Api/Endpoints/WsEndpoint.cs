using System.Net.WebSockets;
using System.Text;
using FlightJar.Api.Hosting;

namespace FlightJar.Api.Endpoints;

internal static class WsEndpoint
{
    public static IEndpointRouteBuilder MapWsEndpoint(this IEndpointRouteBuilder app)
    {
        app.Map("/ws", async (HttpContext ctx, SnapshotBroadcaster broadcaster, CurrentSnapshot current, ILogger<Program> log) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var sub = broadcaster.Subscribe();
            try
            {
                await SendAsync(socket, current.Json, ctx.RequestAborted);
                await foreach (var payload in sub.ReadAllAsync(ctx.RequestAborted))
                {
                    await SendAsync(socket, payload, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                log.LogDebug(ex, "ws client disconnected");
            }
            finally
            {
                broadcaster.Unsubscribe(sub.Id);
            }
            return Results.Empty;
        });

        return app;
    }

    private static async Task SendAsync(WebSocket socket, string payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}
