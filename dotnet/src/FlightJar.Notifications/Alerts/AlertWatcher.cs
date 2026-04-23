using System.Collections.Concurrent;
using FlightJar.Core.State;
using FlightJar.Persistence.Watchlist;

namespace FlightJar.Notifications.Alerts;

/// <summary>
/// Snapshot-driven alert generator. Mirrors <c>app/alerts.py</c>. Fires on
/// watchlist hits (30 min per-tail cooldown) and emergency squawks
/// (5 min cooldown).
/// </summary>
public sealed class AlertWatcher
{
    public static readonly TimeSpan WatchlistCooldown = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan EmergencyCooldown = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyDictionary<string, string> EmergencySquawks = new Dictionary<string, string>
    {
        ["7500"] = "hijack",
        ["7600"] = "radio failure",
        ["7700"] = "general emergency",
    };

    private readonly WatchlistStore _watchlist;
    private readonly NotifierDispatcher _notifier;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _watchlistCooldown = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _emergencyCooldown = new();

    public AlertWatcher(WatchlistStore watchlist, NotifierDispatcher notifier, TimeProvider? time = null)
    {
        _watchlist = watchlist;
        _notifier = notifier;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Process one snapshot. Fires alerts for watchlisted tails + emergency squawks
    /// whose per-tail cooldowns have elapsed.</summary>
    public async Task ObserveAsync(RegistrySnapshot snap, CancellationToken ct = default)
    {
        if (!_notifier.Enabled)
        {
            return;
        }
        var now = _time.GetUtcNow();
        foreach (var ac in snap.Aircraft)
        {
            if (string.IsNullOrEmpty(ac.Icao))
            {
                continue;
            }
            var icao = ac.Icao.ToLowerInvariant();

            if (_watchlist.Contains(icao))
            {
                await MaybeFireAsync(
                    icao, ac, _watchlistCooldown, WatchlistCooldown, now,
                    BuildWatchlistAlert, AlertCategory.Watchlist, ct);
            }

            if (ac.Squawk is string sq && EmergencySquawks.ContainsKey(sq))
            {
                await MaybeFireAsync(
                    icao, ac, _emergencyCooldown, EmergencyCooldown, now,
                    BuildEmergencyAlert, AlertCategory.Emergency, ct);
            }
        }
    }

    private async Task MaybeFireAsync(
        string icao,
        SnapshotAircraft ac,
        ConcurrentDictionary<string, DateTimeOffset> cooldowns,
        TimeSpan cooldown,
        DateTimeOffset now,
        Func<SnapshotAircraft, NotificationMessage> build,
        AlertCategory category,
        CancellationToken ct)
    {
        if (cooldowns.TryGetValue(icao, out var last) && now - last < cooldown)
        {
            return;
        }
        cooldowns[icao] = now;
        var msg = build(ac) with { Url = FlightAwareUrl(icao) };
        await _notifier.DispatchAsync(msg, category, ct);
    }

    private static NotificationMessage BuildWatchlistAlert(SnapshotAircraft ac)
    {
        var icao = (ac.Icao ?? "").ToLowerInvariant();
        var label = !string.IsNullOrEmpty(ac.Callsign) ? ac.Callsign
                  : !string.IsNullOrEmpty(ac.Registration) ? ac.Registration
                  : icao.ToUpperInvariant();
        var title = $"Watchlist: {label}";
        var facts = string.Join(" · ", AlertFacts(ac));
        var body = facts.Length > 0 ? facts : "in range";
        return new NotificationMessage(title, body, AlertLevel.Info);
    }

    private static NotificationMessage BuildEmergencyAlert(SnapshotAircraft ac)
    {
        var icao = (ac.Icao ?? "").ToLowerInvariant();
        var label = !string.IsNullOrEmpty(ac.Callsign) ? ac.Callsign
                  : !string.IsNullOrEmpty(ac.Registration) ? ac.Registration
                  : icao.ToUpperInvariant();
        var squawk = ac.Squawk ?? "????";
        var reason = EmergencySquawks.TryGetValue(squawk, out var r) ? r : "emergency";
        var title = $"⚠️ Emergency {squawk} — {label}";
        var parts = new List<string> { $"Squawking {squawk} ({reason})" };
        parts.AddRange(AlertFacts(ac));
        return new NotificationMessage(title, string.Join(" · ", parts), AlertLevel.Emergency);
    }

    private static IEnumerable<string> AlertFacts(SnapshotAircraft ac)
    {
        if (!string.IsNullOrEmpty(ac.Registration))
        {
            yield return ac.Registration;
        }
        if (!string.IsNullOrEmpty(ac.TypeLong))
        {
            yield return ac.TypeLong;
        }
        if (ac.Altitude is int alt)
        {
            yield return $"{alt:N0} ft";
        }
    }

    private static string FlightAwareUrl(string icao) =>
        $"https://flightaware.com/live/modes/{icao}/redirect";
}
