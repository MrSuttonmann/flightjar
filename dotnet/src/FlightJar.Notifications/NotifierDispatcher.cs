using FlightJar.Persistence.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Notifications;

/// <summary>Fans alerts out to every enabled channel that opts in to the
/// given <see cref="AlertCategory"/>. Reads channels from the config store
/// on every dispatch so UI edits take effect immediately without a reload
/// signal. Mirrors <c>app/notifications.py:NotifierDispatcher</c>.</summary>
public sealed class NotifierDispatcher
{
    private readonly NotificationsConfigStore _config;
    private readonly IReadOnlyDictionary<NotificationChannelType, INotifier> _notifiers;
    private readonly ILogger _logger;

    public NotifierDispatcher(
        NotificationsConfigStore config,
        IEnumerable<INotifier> notifiers,
        ILogger<NotifierDispatcher>? logger = null)
    {
        _config = config;
        _notifiers = notifiers.ToDictionary(n => n.Kind);
        _logger = logger ?? NullLogger<NotifierDispatcher>.Instance;
    }

    /// <summary>True when at least one channel is enabled + ready. Lets the
    /// alert watcher skip per-aircraft cooldown bookkeeping when nothing
    /// could fire.</summary>
    public bool Enabled => _config.Channels.Any(c => c.Enabled && c.IsReady());

    /// <summary>Human-friendly summary like ["telegramx2", "webhookx1"]
    /// — for a startup info log confirming channels loaded.</summary>
    public IReadOnlyList<string> ConfiguredSummary()
    {
        var counts = new Dictionary<NotificationChannelType, int>();
        foreach (var c in _config.Channels)
        {
            if (c.Enabled && c.IsReady())
            {
                counts[c.Type] = counts.GetValueOrDefault(c.Type) + 1;
            }
        }
        return counts
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key.ToString().ToLowerInvariant()}x{kv.Value}")
            .ToList();
    }

    /// <summary>Fan out an alert. Returns the number of channels that
    /// received the send call (regardless of whether the upstream API
    /// succeeded — per-channel failures are logged inside the notifier).</summary>
    public async Task<int> DispatchAsync(
        NotificationMessage msg,
        AlertCategory category,
        CancellationToken ct = default)
    {
        var targets = new List<(INotifier Notifier, NotificationChannel Channel)>();
        foreach (var c in _config.Channels)
        {
            if (!c.Enabled || !ChannelAcceptsCategory(c, category))
            {
                continue;
            }
            if (!_notifiers.TryGetValue(c.Type, out var notifier) || !c.IsReady())
            {
                continue;
            }
            targets.Add((notifier, c));
        }
        if (targets.Count == 0)
        {
            return 0;
        }
        // Fan out in parallel — a slow / stuck upstream for one channel must
        // not delay the others. Individual notifiers swallow + log their own
        // errors; Task.WhenAll wouldn't propagate them anyway.
        var tasks = targets.Select(t => SafeSendAsync(t.Notifier, msg, t.Channel, ct));
        await Task.WhenAll(tasks);
        return targets.Count;
    }

    /// <summary>Send a one-off "hello" through a single channel. Used by the
    /// UI's Test button so users can verify a token/URL without a live event.
    /// Returns true when the channel was found + ready.</summary>
    public async Task<bool> TestChannelAsync(string channelId, CancellationToken ct = default)
    {
        var channel = _config.Find(channelId);
        if (channel is null || !channel.IsReady())
        {
            return false;
        }
        if (!_notifiers.TryGetValue(channel.Type, out var notifier))
        {
            return false;
        }
        var msg = new NotificationMessage(
            Title: "Flightjar test alert",
            Body: $"Test message from the {channel.Type.ToString().ToLowerInvariant()} channel"
                  + $" '{channel.Name}'. If you can see this, it's wired up.");
        await SafeSendAsync(notifier, msg, channel, ct);
        return true;
    }

    private async Task SafeSendAsync(
        INotifier notifier, NotificationMessage msg, NotificationChannel channel, CancellationToken ct)
    {
        try
        {
            await notifier.SendAsync(msg, channel, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "notifier {Kind} failed for channel {Id}", notifier.Kind, channel.Id);
        }
    }

    private static bool ChannelAcceptsCategory(NotificationChannel channel, AlertCategory category) =>
        category switch
        {
            AlertCategory.Watchlist => channel.WatchlistEnabled,
            AlertCategory.Emergency => channel.EmergencyEnabled,
            _ => true,
        };
}
