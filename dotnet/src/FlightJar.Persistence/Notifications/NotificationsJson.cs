using System.Text.Json;

namespace FlightJar.Persistence.Notifications;

/// <summary>
/// Single source of truth for the JSON serializer options used to read /
/// write notification config — both on disk
/// (<see cref="NotificationsConfigStore"/>) and over the HTTP API
/// (<c>/api/notifications/config</c>). Pin the converter here so the two
/// surfaces can never drift.
/// </summary>
public static class NotificationsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
