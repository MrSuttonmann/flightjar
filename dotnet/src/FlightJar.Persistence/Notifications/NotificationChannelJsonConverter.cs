using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlightJar.Persistence.Notifications;

/// <summary>
/// Serialises <see cref="NotificationChannel"/> to/from the v1 flat
/// wire shape — a single JSON object with snake_case keys, discriminated
/// by a <c>type</c> string field. Adding a new channel type means one
/// branch each in <see cref="Read"/> and <see cref="Write"/>.
/// </summary>
/// <remarks>
/// Why a custom converter instead of <c>[JsonDerivedType]</c>: STJ's
/// built-in polymorphism emits the discriminator at a fixed position
/// in the output object and uses <c>$type</c> by default. Even with
/// <c>TypeDiscriminatorPropertyName = "type"</c> the byte order shifts
/// vs the legacy v1 file, and on the read side missing-discriminator
/// payloads bring extra ceremony. A hand-rolled converter gives us
/// full control of property order on write, lenient typed reads, and
/// keeps the format identical to what the frontend
/// (<c>app/static/alerts_dialog.js</c>) already sends.
/// </remarks>
public sealed class NotificationChannelJsonConverter : JsonConverter<NotificationChannel>
{
    public override NotificationChannel? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var typeStr = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        if (typeStr is null)
        {
            return null;
        }
        var kind = typeStr.ToLowerInvariant() switch
        {
            "telegram" => (NotificationChannelType?)NotificationChannelType.Telegram,
            "ntfy" => NotificationChannelType.Ntfy,
            "webhook" => NotificationChannelType.Webhook,
            _ => null,
        };
        if (kind is null)
        {
            return null;
        }

        var id = ReadString(root, "id");
        var name = ReadString(root, "name");
        var enabled = ReadBool(root, "enabled", defaultValue: true);
        var watchlistEnabled = ReadBool(root, "watchlist_enabled", defaultValue: true);
        var emergencyEnabled = ReadBool(root, "emergency_enabled", defaultValue: true);

        return kind switch
        {
            NotificationChannelType.Telegram => new TelegramChannel
            {
                Id = id ?? "",
                Name = name ?? "",
                Enabled = enabled,
                WatchlistEnabled = watchlistEnabled,
                EmergencyEnabled = emergencyEnabled,
                BotToken = ReadString(root, "bot_token") ?? "",
                ChatId = ReadString(root, "chat_id") ?? "",
            },
            NotificationChannelType.Ntfy => new NtfyChannel
            {
                Id = id ?? "",
                Name = name ?? "",
                Enabled = enabled,
                WatchlistEnabled = watchlistEnabled,
                EmergencyEnabled = emergencyEnabled,
                Url = ReadString(root, "url") ?? "",
                Token = ReadString(root, "token") ?? "",
            },
            NotificationChannelType.Webhook => new WebhookChannel
            {
                Id = id ?? "",
                Name = name ?? "",
                Enabled = enabled,
                WatchlistEnabled = watchlistEnabled,
                EmergencyEnabled = emergencyEnabled,
                Url = ReadString(root, "url") ?? "",
            },
            _ => null,
        };
    }

    public override void Write(
        Utf8JsonWriter writer, NotificationChannel value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        // Common fields first, in legacy v1 order.
        writer.WriteString("id", value.Id);
        writer.WriteString("type", TypeToString(value.Type));
        writer.WriteString("name", value.Name);
        writer.WriteBoolean("enabled", value.Enabled);
        writer.WriteBoolean("watchlist_enabled", value.WatchlistEnabled);
        writer.WriteBoolean("emergency_enabled", value.EmergencyEnabled);

        switch (value)
        {
            case TelegramChannel tg:
                writer.WriteString("bot_token", tg.BotToken);
                writer.WriteString("chat_id", tg.ChatId);
                break;
            case NtfyChannel n:
                writer.WriteString("url", n.Url);
                writer.WriteString("token", n.Token);
                break;
            case WebhookChannel w:
                writer.WriteString("url", w.Url);
                break;
        }

        writer.WriteEndObject();
    }

    private static string TypeToString(NotificationChannelType type) => type switch
    {
        NotificationChannelType.Telegram => "telegram",
        NotificationChannelType.Ntfy => "ntfy",
        NotificationChannelType.Webhook => "webhook",
        _ => type.ToString().ToLowerInvariant(),
    };

    private static string? ReadString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool ReadBool(JsonElement obj, string key, bool defaultValue) =>
        obj.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : defaultValue;
}
