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
    public void Replace_DropsUnknownType()
    {
        var store = new NotificationsConfigStore();
        // This test can't naturally construct an "unknown" enum from C# — use
        // the "null" entry pattern to verify the store defends the invariant.
        var channels = store.Replace(new NotificationChannel?[] { null });
        Assert.Empty(channels);
    }

    [Fact]
    public void Replace_AssignsMissingId()
    {
        var store = new NotificationsConfigStore();
        var channels = store.Replace(new[]
        {
            new NotificationChannel { Id = "", Type = NotificationChannelType.Telegram },
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
            new NotificationChannel { Id = "x", Type = NotificationChannelType.Ntfy },
        });
        Assert.Equal("ntfy channel", channels[0].Name);
    }

    [Fact]
    public void Replace_StripsCrossTypeFields()
    {
        var store = new NotificationsConfigStore();
        var channels = store.Replace(new[]
        {
            new NotificationChannel
            {
                Id = "x",
                Type = NotificationChannelType.Webhook,
                Url = "https://example.com/hook",
                BotToken = "leaked-from-a-telegram-config",
                ChatId = "leaked",
            },
        });
        Assert.Equal("https://example.com/hook", channels[0].Url);
        Assert.Equal("", channels[0].BotToken);
        Assert.Equal("", channels[0].ChatId);
    }

    [Fact]
    public void Replace_DedupsIds()
    {
        var store = new NotificationsConfigStore();
        var channels = store.Replace(new[]
        {
            new NotificationChannel { Id = "same", Type = NotificationChannelType.Telegram, BotToken = "a", ChatId = "1" },
            new NotificationChannel { Id = "same", Type = NotificationChannelType.Ntfy, Url = "https://ntfy/x" },
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
            new NotificationChannel { Id = "abc", Type = NotificationChannelType.Webhook, Url = "https://hook" },
        });
        var found = store.Find("abc");
        Assert.NotNull(found);
        Assert.Equal("https://hook", found!.Url);
        Assert.Null(store.Find("nope"));
    }

    [Fact]
    public void IsReady_ValidatesRequiredFields()
    {
        Assert.False(new NotificationChannel { Id = "x", Type = NotificationChannelType.Telegram }.IsReady());
        Assert.False(new NotificationChannel { Id = "x", Type = NotificationChannelType.Telegram, BotToken = "t" }.IsReady());
        Assert.True(new NotificationChannel { Id = "x", Type = NotificationChannelType.Telegram, BotToken = "t", ChatId = "c" }.IsReady());
        Assert.False(new NotificationChannel { Id = "x", Type = NotificationChannelType.Ntfy }.IsReady());
        Assert.True(new NotificationChannel { Id = "x", Type = NotificationChannelType.Ntfy, Url = "u" }.IsReady());
    }

    [Fact]
    public async Task Persist_ReloadsCleanly()
    {
        var store = new NotificationsConfigStore(PathFor());
        store.Replace(new[]
        {
            new NotificationChannel
            {
                Id = "tg1",
                Type = NotificationChannelType.Telegram,
                Name = "Phone",
                BotToken = "123:ABC",
                ChatId = "42",
            },
        });

        var reload = new NotificationsConfigStore(PathFor());
        await reload.LoadAsync();
        Assert.Single(reload.Channels);
        var c = reload.Channels[0];
        Assert.Equal(NotificationChannelType.Telegram, c.Type);
        Assert.Equal("Phone", c.Name);
        Assert.Equal("123:ABC", c.BotToken);
        Assert.Equal("42", c.ChatId);
    }

    [Fact]
    public async Task Load_AcceptsStringTypeField()
    {
        // Files written by the legacy Python backend (and by the .NET HTTP
        // API serializer, which uses snake_case JsonStringEnumConverter)
        // store the channel type as a string. The store must accept that
        // shape on disk — otherwise an upgrade silently drops every
        // configured channel and the user loses their notification setup.
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
        var tg = store.Channels.Single(c => c.Id == "tg1");
        Assert.Equal(NotificationChannelType.Telegram, tg.Type);
        Assert.Equal("123:ABC", tg.BotToken);
        Assert.Equal("42", tg.ChatId);
        var wh = store.Channels.Single(c => c.Id == "wh1");
        Assert.Equal(NotificationChannelType.Webhook, wh.Type);
        Assert.Equal("https://hass.example/hook", wh.Url);
        Assert.False(wh.EmergencyEnabled);
    }

    [Fact]
    public void Persist_WritesTypeAsSnakeCaseString()
    {
        // Writer stays compatible with the legacy/HTTP shape: string-valued
        // snake_case enum, not the default integer fallback. Locks in the
        // round-trip so a future change to JsonOpts that drops the
        // converter is caught here.
        var store = new NotificationsConfigStore(PathFor());
        store.Replace(new[]
        {
            new NotificationChannel { Id = "ntfy1", Type = NotificationChannelType.Ntfy, Url = "https://ntfy.sh/x" },
        });

        var raw = File.ReadAllText(PathFor());
        Assert.Contains("\"type\":\"ntfy\"", raw);
        Assert.DoesNotContain("\"type\":1", raw);
    }
}
