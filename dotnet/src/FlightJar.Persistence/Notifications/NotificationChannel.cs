using System.Text.Json.Serialization;

namespace FlightJar.Persistence.Notifications;

public enum NotificationChannelType
{
    Telegram,
    Ntfy,
    Webhook,
}

/// <summary>
/// One user-configured alert channel. Polymorphic on
/// <see cref="NotificationChannelType"/>: each subclass owns the fields
/// + readiness rules for its kind, so adding a new type means a new
/// subclass plus one branch in
/// <see cref="NotificationChannelJsonConverter"/>.
/// </summary>
/// <remarks>
/// Wire format stays flat per v1: keys live at the top-level JSON object
/// (no nested config bag), discriminated by a snake_case <c>type</c>
/// field. The frontend at <c>app/static/alerts_dialog.js</c> reads /
/// writes the same flat shape via its <c>FIELDS[type]</c> map.
/// </remarks>
[JsonConverter(typeof(NotificationChannelJsonConverter))]
public abstract record NotificationChannel
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool WatchlistEnabled { get; init; } = true;
    public bool EmergencyEnabled { get; init; } = true;

    public abstract NotificationChannelType Type { get; }

    /// <summary>True when all required fields for this channel's type are filled in.</summary>
    public abstract bool IsReady();
}

/// <summary>Telegram bot channel — needs a <c>bot_token</c> and <c>chat_id</c>.</summary>
public sealed record TelegramChannel : NotificationChannel
{
    public override NotificationChannelType Type => NotificationChannelType.Telegram;
    public string BotToken { get; init; } = "";
    public string ChatId { get; init; } = "";

    public override bool IsReady() =>
        !string.IsNullOrEmpty(BotToken) && !string.IsNullOrEmpty(ChatId);
}

/// <summary>ntfy topic — needs a <c>url</c>; <c>token</c> is optional bearer auth.</summary>
public sealed record NtfyChannel : NotificationChannel
{
    public override NotificationChannelType Type => NotificationChannelType.Ntfy;
    public string Url { get; init; } = "";
    public string Token { get; init; } = "";

    public override bool IsReady() => !string.IsNullOrEmpty(Url);
}

/// <summary>Generic JSON webhook — needs a <c>url</c>, no auth surface today.</summary>
public sealed record WebhookChannel : NotificationChannel
{
    public override NotificationChannelType Type => NotificationChannelType.Webhook;
    public string Url { get; init; } = "";

    public override bool IsReady() => !string.IsNullOrEmpty(Url);
}
