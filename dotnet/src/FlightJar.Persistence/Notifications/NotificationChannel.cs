namespace FlightJar.Persistence.Notifications;

public enum NotificationChannelType
{
    Telegram,
    Ntfy,
    Webhook,
}

/// <summary>
/// One user-configured alert channel. Mirrors the dict shape
/// <c>app/notifications_config.py</c> persists. Type-specific fields
/// live on the same record (nullable); non-applicable fields stay empty.
/// </summary>
public sealed record NotificationChannel
{
    public required string Id { get; init; }
    public required NotificationChannelType Type { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool WatchlistEnabled { get; init; } = true;
    public bool EmergencyEnabled { get; init; } = true;

    // Telegram-specific
    public string BotToken { get; init; } = "";
    public string ChatId { get; init; } = "";

    // Ntfy/webhook-specific
    public string Url { get; init; } = "";
    /// <summary>Bearer token — ntfy only. Webhooks currently don't support auth.</summary>
    public string Token { get; init; } = "";

    /// <summary>True when all required fields for this channel's type are filled in.</summary>
    public bool IsReady() => Type switch
    {
        NotificationChannelType.Telegram => !string.IsNullOrEmpty(BotToken) && !string.IsNullOrEmpty(ChatId),
        NotificationChannelType.Ntfy => !string.IsNullOrEmpty(Url),
        NotificationChannelType.Webhook => !string.IsNullOrEmpty(Url),
        _ => false,
    };
}
