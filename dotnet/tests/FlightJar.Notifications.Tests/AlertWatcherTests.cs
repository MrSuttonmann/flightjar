using System.Net;
using FlightJar.Core.State;
using FlightJar.Notifications.Alerts;
using FlightJar.Notifications.Tests.Mocks;
using FlightJar.Persistence.Notifications;
using FlightJar.Persistence.Watchlist;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightJar.Notifications.Tests;

public class AlertWatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 23, 12, 0, 0, TimeSpan.Zero);

    private static (AlertWatcher Watcher, WatchlistStore Wlist, NotifierDispatcher Dispatcher,
                    NotificationsConfigStore Cfg, MockHttpMessageHandler Handler, FakeTimeProvider Time)
        Setup()
    {
        var handler = new MockHttpMessageHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) };
        var http = new HttpClient(handler);
        var time = new FakeTimeProvider(T0);
        var wl = new WatchlistStore(time: time);
        var cfg = new NotificationsConfigStore();
        cfg.Replace(new[]
        {
            new NotificationChannel { Id = "h", Type = NotificationChannelType.Webhook, Url = "https://hook.example/x" },
        });
        var dispatcher = new NotifierDispatcher(
            cfg,
            new INotifier[]
            {
                new TelegramNotifier(http, NullLogger<TelegramNotifier>.Instance),
                new NtfyNotifier(http, NullLogger<NtfyNotifier>.Instance),
                new WebhookNotifier(http, NullLogger<WebhookNotifier>.Instance),
            });
        var watcher = new AlertWatcher(wl, dispatcher, time);
        return (watcher, wl, dispatcher, cfg, handler, time);
    }

    private static RegistrySnapshot SnapshotWith(params SnapshotAircraft[] aircraft) =>
        new(Now: 0, Count: aircraft.Length, Positioned: 0, Receiver: null, SiteName: null,
            Aircraft: aircraft);

    [Fact]
    public async Task NoChannels_Configured_SkipsAltogether()
    {
        var (watcher, wl, _, cfg, handler, _) = Setup();
        cfg.Replace(Array.Empty<NotificationChannel>());
        wl.Replace(new[] { "abc123" });
        await watcher.ObserveAsync(SnapshotWith(new SnapshotAircraft
        {
            Icao = "abc123",
            Callsign = "FLY1",
        }));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task WatchlistHit_FiresOnce_WithinCooldown()
    {
        var (watcher, wl, _, _, handler, time) = Setup();
        wl.Replace(new[] { "abc123" });
        var ac = new SnapshotAircraft { Icao = "abc123", Callsign = "FLY1" };
        await watcher.ObserveAsync(SnapshotWith(ac));
        await watcher.ObserveAsync(SnapshotWith(ac));
        Assert.Equal(1, handler.CallCount);

        // After the cooldown window, it fires again.
        time.Advance(AlertWatcher.WatchlistCooldown + TimeSpan.FromSeconds(1));
        await watcher.ObserveAsync(SnapshotWith(ac));
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task EmergencySquawk_Fires()
    {
        var (watcher, _, _, _, handler, _) = Setup();
        var ac = new SnapshotAircraft { Icao = "abc123", Callsign = "FLY1", Squawk = "7700" };
        await watcher.ObserveAsync(SnapshotWith(ac));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task EmergencySquawk_HasSeparateCooldownFromWatchlist()
    {
        var (watcher, wl, _, _, handler, time) = Setup();
        wl.Replace(new[] { "abc123" });
        var ac = new SnapshotAircraft { Icao = "abc123", Callsign = "FLY1", Squawk = "7700" };
        await watcher.ObserveAsync(SnapshotWith(ac));
        // First tick: both watchlist + emergency fire (2 dispatches).
        Assert.Equal(2, handler.CallCount);

        // 10 minutes later — within watchlist cooldown, past emergency cooldown.
        time.Advance(TimeSpan.FromMinutes(10));
        await watcher.ObserveAsync(SnapshotWith(ac));
        Assert.Equal(3, handler.CallCount); // only emergency fires again
    }

    [Fact]
    public async Task NonEmergencySquawk_DoesNotFire()
    {
        var (watcher, _, _, _, handler, _) = Setup();
        await watcher.ObserveAsync(SnapshotWith(new SnapshotAircraft
        {
            Icao = "abc123",
            Callsign = "FLY1",
            Squawk = "1200",
        }));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task PerTailCooldown_DoesNotAffectOtherTails()
    {
        var (watcher, wl, _, _, handler, _) = Setup();
        wl.Replace(new[] { "aaa111", "bbb222" });
        await watcher.ObserveAsync(SnapshotWith(
            new SnapshotAircraft { Icao = "aaa111", Callsign = "A" },
            new SnapshotAircraft { Icao = "bbb222", Callsign = "B" }));
        // Both fire on first tick, despite sharing the watchlist cooldown dict.
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task NonWatchlistedTail_DoesNotFire()
    {
        var (watcher, wl, _, _, handler, _) = Setup();
        wl.Replace(new[] { "aaa111" });
        await watcher.ObserveAsync(SnapshotWith(new SnapshotAircraft
        {
            Icao = "bbb222",
            Callsign = "FLY1",
        }));
        Assert.Equal(0, handler.CallCount);
    }
}
