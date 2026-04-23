namespace FlightJar.Notifications;

public enum AlertCategory
{
    Watchlist,
    Emergency,
}

public enum AlertLevel
{
    Info,
    Warning,
    Emergency,
}

/// <summary>One alert to dispatch. The dispatcher fans this out across every
/// configured channel that opts into the matching <see cref="AlertCategory"/>.</summary>
public sealed record NotificationMessage(
    string Title,
    string Body,
    AlertLevel Level = AlertLevel.Info,
    string? Url = null,
    string? PhotoUrl = null);
