using System.Text.Json;
using FlightJar.Api.Auth;
using FlightJar.Notifications;
using FlightJar.Persistence.Notifications;

namespace FlightJar.Api.Endpoints;

internal static class NotificationsEndpoints
{
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications/config", (NotificationsConfigStore store) =>
            Results.Json(new
            {
                version = NotificationsConfigStore.SchemaVersion,
                channels = store.Channels,
            })).RequireAuthSession();

        app.MapPost("/api/notifications/config", async (HttpContext ctx, NotificationsConfigStore store) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("channels", out var a)
                ? a
                : root;
            if (arr.ValueKind != JsonValueKind.Array)
            {
                return Results.BadRequest(new { error = "expected array or {channels: array}" });
            }
            if (arr.GetArrayLength() > 100)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            var channels = new List<NotificationChannel?>();
            foreach (var item in arr.EnumerateArray())
            {
                channels.Add(item.Deserialize<NotificationChannel>(NotificationsJson.Options));
            }
            var updated = store.Replace(channels);
            return Results.Json(new { channels = updated });
        }).RequireAuthSession();

        app.MapPost("/api/notifications/test/{channelId}",
            async (string channelId, NotifierDispatcher dispatcher, CancellationToken ct) =>
            {
                var ok = await dispatcher.TestChannelAsync(channelId, ct);
                return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "unknown channel" });
            }).RequireAuthSession();

        return app;
    }
}
