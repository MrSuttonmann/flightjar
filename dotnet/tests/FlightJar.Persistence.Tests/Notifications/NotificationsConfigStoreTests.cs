using System.Text.Json;
using FlightJar.Persistence.Notifications;

namespace FlightJar.Persistence.Tests.Notifications;

public class NotificationsConfigStoreTests : IDisposable
{
    private readonly string _tmp;

    public NotificationsConfigStoreTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"flightjar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp))
        {
            Directory.Delete(_tmp, recursive: true);
        }
    }

    private string PathFor(string file = "notifications.json") => Path.Combine(_tmp, file);

    [Fact]
    public void Replace_DropsNullEntries()
    {
        var store = new NotificationsConfigStore();
        var channels = store.Replace(new NotificationChannel?[] { null });
        Assert.Empty(channels);
    }

    [Fact]
    public void Replace_AssignsMissingId()
    {
        var store = new NotificationsConfigStore();
        var channels = store.Replace(new[]
        {
            new TelegramChannel { Id = "" },
        });
        Assert.Single(channels);
        Assert.False(string.IsNullOrEmpty(channels[0].Id));
    }

    [Fact]
    public void Replace_AssignsDefaultName()
    {
        var store = new NotificationsConfigStore();
        var channels = store.Replace(new[]
        {
            new NtfyChannel { Id = "x" },
        });
        Assert.Equal("ntfy channel", channels[0].Name);
    }

    [Fact]
    public async Task Replace_StripsCrossTypeFieldsAtJsonBoundary()
    {
        // Polymorphism makes cross-type leaks impossible at construction
        // time — TelegramChannel literally doesn't have a Url property —
        // so the in-memory test we replaced was tautological. The real
        // contract is that legacy / hand-edited JSON with leaked fields
        // round-trips without those fields surviving the load.
        var path = PathFor();
        File.WriteAllText(path, """
            {
              "version": 1,
              "channels": [
                {
                  "id": "x",
                  "type": "webhook",
                  "name": "leaky",
                  "enabled": true,
                  "watchlist_enabled": true,
                  "emergency_enabled": true,
                  "url": "https://example.com/hook",
                  "bot_token": "leaked-from-a-telegram-config",
                  "chat_id": "leaked"
                }
              ]
            }
            """);

        var store = new NotificationsConfigStore(path);
        await store.LoadAsync();

        var c = Assert.Single(store.Channels);
        var w = Assert.IsType<WebhookChannel>(c);
        Assert.Equal("https://example.com/hook", w.Url);
        // bot_token / chat_id never made it into a typed property.
    }

    [Fact]
    public void Replace_DedupsIds()
    {
        var store = new NotificationsConfigStore();
        var channels = store.Replace(new NotificationChannel[]
        {
            new TelegramChannel { Id = "same", BotToken = "a", ChatId = "1" },
            new NtfyChannel { Id = "same", Url = "https://ntfy/x" },
        });
        Assert.Equal(2, channels.Count);
        Assert.NotEqual(channels[0].Id, channels[1].Id);
    }

    [Fact]
    public void Find_ReturnsChannelById()
    {
        var store = new NotificationsConfigStore();
        store.Replace(new[]
        {
            new WebhookChannel { Id = "abc", Url = "https://hook" },
        });
        var found = Assert.IsType<WebhookChannel>(store.Find("abc"));
        Assert.Equal("https://hook", found.Url);
        Assert.Null(store.Find("nope"));
    }

    [Fact]
    public void IsReady_ValidatesRequiredFields()
    {
        Assert.False(new TelegramChannel { Id = "x" }.IsReady());
        Assert.False(new TelegramChannel { Id = "x", BotToken = "t" }.IsReady());
        Assert.True(new TelegramChannel { Id = "x", BotToken = "t", ChatId = "c" }.IsReady());
        Assert.False(new NtfyChannel { Id = "x" }.IsReady());
        Assert.True(new NtfyChannel { Id = "x", Url = "u" }.IsReady());
        Assert.False(new WebhookChannel { Id = "x" }.IsReady());
        Assert.True(new WebhookChannel { Id = "x", Url = "u" }.IsReady());
    }

    [Fact]
    public async Task Persist_ReloadsCleanly()
    {
        var store = new NotificationsConfigStore(PathFor());
        store.Replace(new[]
        {
            new TelegramChannel
            {
                Id = "tg1",
                Name = "Phone",
                BotToken = "123:ABC",
                ChatId = "42",
            },
        });

        var reload = new NotificationsConfigStore(PathFor());
        await reload.LoadAsync();
        Assert.Single(reload.Channels);
        var c = Assert.IsType<TelegramChannel>(reload.Channels[0]);
        Assert.Equal("Phone", c.Name);
        Assert.Equal("123:ABC", c.BotToken);
        Assert.Equal("42", c.ChatId);
    }

    [Fact]
    public async Task Load_AcceptsLegacyV1WireFormat()
    {
        // Files written by the legacy backend (and by the .NET HTTP API
        // serializer) store the channel type as a snake_case string. The
        // store must accept that shape on disk — otherwise an upgrade
        // silently drops every configured channel and the user loses
        // their notification setup.
        var path = PathFor();
        File.WriteAllText(path, """
            {
              "version": 1,
              "channels": [
                {
                  "id": "tg1",
                  "type": "telegram",
                  "name": "Phone",
                  "enabled": true,
                  "watchlist_enabled": true,
                  "emergency_enabled": true,
                  "bot_token": "123:ABC",
                  "chat_id": "42"
                },
                {
                  "id": "wh1",
                  "type": "webhook",
                  "name": "HA bridge",
                  "enabled": true,
                  "watchlist_enabled": true,
                  "emergency_enabled": false,
                  "url": "https://hass.example/hook"
                }
              ]
            }
            """);

        var store = new NotificationsConfigStore(path);
        await store.LoadAsync();

        Assert.Equal(2, store.Channels.Count);
        var tg = Assert.IsType<TelegramChannel>(store.Channels.Single(c => c.Id == "tg1"));
        Assert.Equal("123:ABC", tg.BotToken);
        Assert.Equal("42", tg.ChatId);
        var wh = Assert.IsType<WebhookChannel>(store.Channels.Single(c => c.Id == "wh1"));
        Assert.Equal("https://hass.example/hook", wh.Url);
        Assert.False(wh.EmergencyEnabled);
    }

    [Fact]
    public void Persist_WritesTypeAsSnakeCaseString()
    {
        // Writer stays compatible with the legacy/HTTP shape: string-valued
        // snake_case enum, not the default integer fallback. Locks in the
        // round-trip so a future converter regression that drops the
        // string discriminator is caught here.
        var store = new NotificationsConfigStore(PathFor());
        store.Replace(new[]
        {
            new NtfyChannel { Id = "ntfy1", Url = "https://ntfy.sh/x" },
        });

        var raw = File.ReadAllText(PathFor());
        Assert.Contains("\"type\":\"ntfy\"", raw);
        Assert.DoesNotContain("\"type\":1", raw);
    }

    [Fact]
    public async Task RoundTrip_PreservesAllUserMeaningfulData()
    {
        // Polymorphic save no longer emits empty cross-type fields — that's
        // a behaviour improvement, not a regression — so a strict byte-equal
        // round-trip is the wrong contract. The right one is "every
        // user-visible field survives untouched".
        var fixturePath = PathFor("fixture.json");
        File.WriteAllText(fixturePath, """
            {
              "version": 1,
              "channels": [
                {
                  "id": "tg1",
                  "type": "telegram",
                  "name": "Phone",
                  "enabled": true,
                  "watchlist_enabled": true,
                  "emergency_enabled": true,
                  "bot_token": "123:ABC",
                  "chat_id": "42"
                },
                {
                  "id": "n1",
                  "type": "ntfy",
                  "name": "Topic",
                  "enabled": true,
                  "watchlist_enabled": false,
                  "emergency_enabled": true,
                  "url": "https://ntfy.sh/x",
                  "token": "bearer-secret"
                },
                {
                  "id": "wh1",
                  "type": "webhook",
                  "name": "HA",
                  "enabled": false,
                  "watchlist_enabled": true,
                  "emergency_enabled": true,
                  "url": "https://hass/hook"
                }
              ]
            }
            """);

        var roundTripPath = PathFor("roundtrip.json");
        File.Copy(fixturePath, roundTripPath);
        var first = new NotificationsConfigStore(roundTripPath);
        await first.LoadAsync();
        // Force a persist by replacing with the loaded set.
        first.Replace(first.Channels);

        var second = new NotificationsConfigStore(roundTripPath);
        await second.LoadAsync();

        Assert.Equal(3, second.Channels.Count);
        var tg = Assert.IsType<TelegramChannel>(second.Channels.Single(c => c.Id == "tg1"));
        Assert.Equal("Phone", tg.Name);
        Assert.Equal("123:ABC", tg.BotToken);
        Assert.Equal("42", tg.ChatId);
        var n = Assert.IsType<NtfyChannel>(second.Channels.Single(c => c.Id == "n1"));
        Assert.Equal("https://ntfy.sh/x", n.Url);
        Assert.Equal("bearer-secret", n.Token);
        Assert.False(n.WatchlistEnabled);
        var wh = Assert.IsType<WebhookChannel>(second.Channels.Single(c => c.Id == "wh1"));
        Assert.Equal("https://hass/hook", wh.Url);
        Assert.False(wh.Enabled);
    }

    [Fact]
    public void AcceptsFrontendPostBody()
    {
        // Pin the exact shape app/static/alerts_dialog.js builds + POSTs.
        // FIELDS[type] in that file enumerates the per-type keys; the
        // common wrapper (id/type/name/enabled/watchlist_enabled/
        // emergency_enabled) is supplied per-record. If this test ever
        // breaks, alerts_dialog.js needs a coordinated update.
        var json = """
            {
              "id": "tmp-abc",
              "type": "telegram",
              "name": "",
              "enabled": true,
              "watchlist_enabled": true,
              "emergency_enabled": true,
              "bot_token": "12345:Secret",
              "chat_id": "99"
            }
            """;
        var c = JsonSerializer.Deserialize<NotificationChannel>(json, NotificationsJson.Options);
        var tg = Assert.IsType<TelegramChannel>(c);
        Assert.Equal("tmp-abc", tg.Id);
        Assert.Equal("12345:Secret", tg.BotToken);
        Assert.Equal("99", tg.ChatId);
    }
}
