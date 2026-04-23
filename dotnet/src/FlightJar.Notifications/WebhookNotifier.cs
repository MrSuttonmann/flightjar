using System.Net.Http.Json;
using FlightJar.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace FlightJar.Notifications;

/// <summary>POST an alert as JSON to a user-configured URL. Mirrors
/// <c>app/notifications.py:WebhookNotifier</c>.</summary>
public sealed class WebhookNotifier : INotifier
{
    private readonly HttpClient _http;
    private readonly ILogger<WebhookNotifier> _logger;

    public NotificationChannelType Kind => NotificationChannelType.Webhook;

    public WebhookNotifier(HttpClient http, ILogger<WebhookNotifier> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task SendAsync(NotificationMessage msg, NotificationChannel channel, CancellationToken ct)
    {
        if (channel.Type != NotificationChannelType.Webhook || !channel.IsReady())
        {
            return;
        }
        var payload = new
        {
            title = msg.Title,
            body = msg.Body,
            level = msg.Level.ToString().ToLowerInvariant(),
            url = msg.Url,
            photo_url = msg.PhotoUrl,
        };
        try
        {
            using var resp = await _http.PostAsJsonAsync(channel.Url, payload, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "webhook send failed");
        }
    }
}
