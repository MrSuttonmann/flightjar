using System.Net;
using System.Net.Http.Json;
using FlightJar.Notifications.Tests.Mocks;
using FlightJar.Persistence.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Notifications.Tests;

public class DispatcherTests
{
    private static (NotifierDispatcher Dispatcher, NotificationsConfigStore Config, MockHttpMessageHandler Handler)
        BuildDispatcher()
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var config = new NotificationsConfigStore();
        var dispatcher = new NotifierDispatcher(
            config,
            new INotifier[]
            {
                new TelegramNotifier(http, NullLogger<TelegramNotifier>.Instance),
                new NtfyNotifier(http, NullLogger<NtfyNotifier>.Instance),
                new WebhookNotifier(http, NullLogger<WebhookNotifier>.Instance),
            });
        return (dispatcher, config, handler);
    }

    [Fact]
    public async Task Dispatch_SkipsEmptyConfig()
    {
        var (dispatcher, _, handler) = BuildDispatcher();
        var sent = await dispatcher.DispatchAsync(
            new NotificationMessage("Title", "Body"), AlertCategory.Watchlist);
        Assert.Equal(0, sent);
        Assert.Equal(0, handler.CallCount);
        Assert.False(dispatcher.Enabled);
    }

    [Fact]
    public async Task Dispatch_SkipsDisabledChannels()
    {
        var (dispatcher, config, handler) = BuildDispatcher();
        config.Replace(new[]
        {
            new WebhookChannel
            {
                Id = "a",
                Url = "https://example.com/hook",
                Enabled = false,
            },
        });
        var sent = await dispatcher.DispatchAsync(
            new NotificationMessage("T", "B"), AlertCategory.Emergency);
        Assert.Equal(0, sent);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Dispatch_SkipsChannelsNotOptingIntoCategory()
    {
        var (dispatcher, config, handler) = BuildDispatcher();
        config.Replace(new[]
        {
            new WebhookChannel
            {
                Id = "a",
                Url = "https://example.com/hook",
                WatchlistEnabled = false,
                EmergencyEnabled = true,
            },
        });
        Assert.Equal(0, await dispatcher.DispatchAsync(new("T", "B"), AlertCategory.Watchlist));
        Assert.Equal(1, await dispatcher.DispatchAsync(new("T", "B"), AlertCategory.Emergency));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Dispatch_FansOutAcrossChannelTypes()
    {
        var (dispatcher, config, handler) = BuildDispatcher();
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        config.Replace(new NotificationChannel[]
        {
            new TelegramChannel { Id = "1", BotToken = "t", ChatId = "c" },
            new NtfyChannel { Id = "2", Url = "https://ntfy.sh/x" },
            new WebhookChannel { Id = "3", Url = "https://hook.example/x" },
        });
        var sent = await dispatcher.DispatchAsync(new("T", "B"), AlertCategory.Emergency);
        Assert.Equal(3, sent);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task PerChannelFailure_DoesNotAbortOthers()
    {
        var (dispatcher, config, handler) = BuildDispatcher();
        handler.Handler = req => req.RequestUri!.AbsoluteUri.Contains("telegram.org")
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK);
        config.Replace(new NotificationChannel[]
        {
            new TelegramChannel { Id = "1", BotToken = "t", ChatId = "c" },
            new WebhookChannel { Id = "2", Url = "https://hook.example/x" },
        });
        var sent = await dispatcher.DispatchAsync(new("T", "B"), AlertCategory.Watchlist);
        Assert.Equal(2, sent); // both attempted
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task TestChannel_ReturnsFalseForUnknownId()
    {
        var (dispatcher, _, _) = BuildDispatcher();
        Assert.False(await dispatcher.TestChannelAsync("not-there"));
    }

    [Fact]
    public async Task TestChannel_SendsOnce()
    {
        var (dispatcher, config, handler) = BuildDispatcher();
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        config.Replace(new[]
        {
            new WebhookChannel { Id = "hook", Url = "https://hook.example/x" },
        });
        Assert.True(await dispatcher.TestChannelAsync("hook"));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Telegram_UsesSendPhotoWithPhotoUrl()
    {
        var (dispatcher, config, handler) = BuildDispatcher();
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        config.Replace(new[]
        {
            new TelegramChannel { Id = "t", BotToken = "bot", ChatId = "42" },
        });
        await dispatcher.DispatchAsync(
            new("Hi", "body", PhotoUrl: "https://cdn/pic.jpg"),
            AlertCategory.Watchlist);
        Assert.Single(handler.Requests);
        Assert.Contains("/sendPhoto", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Ntfy_SetsHeaders()
    {
        var (dispatcher, config, handler) = BuildDispatcher();
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        config.Replace(new[]
        {
            new NtfyChannel
            {
                Id = "n",
                Url = "https://ntfy.sh/alerts",
                Token = "secret",
            },
        });
        await dispatcher.DispatchAsync(
            new("Title", "Body", Level: AlertLevel.Emergency, Url: "https://ui/path", PhotoUrl: "https://cdn/pic.jpg"),
            AlertCategory.Emergency);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("Title", req.Headers.GetValues("Title").Single());
        Assert.Equal("urgent", req.Headers.GetValues("Priority").Single());
        Assert.Equal("rotating_light", req.Headers.GetValues("Tags").Single());
        Assert.Equal("https://ui/path", req.Headers.GetValues("Click").Single());
        Assert.Equal("https://cdn/pic.jpg", req.Headers.GetValues("Attach").Single());
        Assert.Equal("Bearer secret", req.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Webhook_SendsJsonPayload()
    {
        var (dispatcher, config, handler) = BuildDispatcher();
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK);
        config.Replace(new[]
        {
            new WebhookChannel { Id = "h", Url = "https://hook.example/x" },
        });
        await dispatcher.DispatchAsync(
            new("T", "B", Level: AlertLevel.Warning),
            AlertCategory.Watchlist);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void ConfiguredSummary_CountsByType()
    {
        var (dispatcher, config, _) = BuildDispatcher();
        config.Replace(new NotificationChannel[]
        {
            new TelegramChannel { Id = "1", BotToken = "t", ChatId = "c" },
            new TelegramChannel { Id = "2", BotToken = "t2", ChatId = "c2" },
            new WebhookChannel { Id = "3", Url = "https://hook" },
        });
        Assert.Equal(new[] { "telegramx2", "webhookx1" }, dispatcher.ConfiguredSummary());
    }
}
