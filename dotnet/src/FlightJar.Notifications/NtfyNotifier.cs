using System.Net.Http.Headers;
using System.Text;
using FlightJar.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace FlightJar.Notifications;

/// <summary>Post to an ntfy topic (ntfy.sh or self-hosted). Title / priority /
/// tags / click / attach ride as HTTP headers; body goes in the request body.
/// Mirrors <c>app/notifications.py:NtfyNotifier</c>.</summary>
public sealed class NtfyNotifier : INotifier
{
    private static readonly IReadOnlyDictionary<AlertLevel, string> Priority = new Dictionary<AlertLevel, string>
    {
        [AlertLevel.Info] = "default",
        [AlertLevel.Warning] = "high",
        [AlertLevel.Emergency] = "urgent",
    };

    private static readonly IReadOnlyDictionary<AlertLevel, string> Tag = new Dictionary<AlertLevel, string>
    {
        [AlertLevel.Info] = "airplane",
        [AlertLevel.Warning] = "warning",
        [AlertLevel.Emergency] = "rotating_light",
    };

    private readonly HttpClient _http;
    private readonly ILogger<NtfyNotifier> _logger;

    public NotificationChannelType Kind => NotificationChannelType.Ntfy;

    public NtfyNotifier(HttpClient http, ILogger<NtfyNotifier> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task SendAsync(NotificationMessage msg, NotificationChannel channel, CancellationToken ct)
    {
        if (channel.Type != NotificationChannelType.Ntfy || !channel.IsReady())
        {
            return;
        }
        var url = channel.Url.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(msg.Body, Encoding.UTF8),
        };
        req.Headers.TryAddWithoutValidation("Title", msg.Title);
        req.Headers.TryAddWithoutValidation("Priority", Priority[msg.Level]);
        req.Headers.TryAddWithoutValidation("Tags", Tag[msg.Level]);
        if (!string.IsNullOrEmpty(msg.Url))
        {
            req.Headers.TryAddWithoutValidation("Click", msg.Url);
        }
        if (!string.IsNullOrEmpty(msg.PhotoUrl))
        {
            req.Headers.TryAddWithoutValidation("Attach", msg.PhotoUrl);
        }
        if (!string.IsNullOrEmpty(channel.Token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", channel.Token);
        }

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ntfy send failed");
        }
    }
}
