using System.Net.Http.Json;
using System.Text;
using FlightJar.Persistence.Notifications;
using Microsoft.Extensions.Logging;

namespace FlightJar.Notifications;

/// <summary>Post to Telegram via the Bot API. Uses MarkdownV2 for title
/// formatting. When a photo is supplied, sendPhoto with caption; otherwise
/// sendMessage. Mirrors <c>app/notifications.py:TelegramNotifier</c>.</summary>
public sealed class TelegramNotifier : INotifier
{
    private readonly HttpClient _http;
    private readonly ILogger<TelegramNotifier> _logger;

    public NotificationChannelType Kind => NotificationChannelType.Telegram;

    public TelegramNotifier(HttpClient http, ILogger<TelegramNotifier> logger)
    {
        _http = http;
        _logger = logger;
    }

    // MarkdownV2 reserved characters per https://core.telegram.org/bots/api#markdownv2-style
    private static readonly HashSet<char> MdV2Reserved = new("_*[]()~`>#+-=|{}.!\\");

    /// <summary>Escape every MarkdownV2 reserved char in <paramref name="text"/>.</summary>
    public static string EscapeMarkdownV2(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (MdV2Reserved.Contains(c))
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public Task SendAsync(NotificationMessage msg, NotificationChannel channel, CancellationToken ct)
    {
        if (channel is not TelegramChannel tg || !tg.IsReady())
        {
            return Task.CompletedTask;
        }
        return SendAsync(msg, tg, ct);
    }

    private async Task SendAsync(NotificationMessage msg, TelegramChannel channel, CancellationToken ct)
    {
        var text = $"*{EscapeMarkdownV2(msg.Title)}*\n{EscapeMarkdownV2(msg.Body)}";
        if (!string.IsNullOrEmpty(msg.Url))
        {
            text += $"\n[Details]({EscapeMarkdownV2(msg.Url)})";
        }

        object payload;
        string api;
        if (!string.IsNullOrEmpty(msg.PhotoUrl))
        {
            api = $"https://api.telegram.org/bot{channel.BotToken}/sendPhoto";
            payload = new
            {
                chat_id = channel.ChatId,
                photo = msg.PhotoUrl,
                caption = text,
                parse_mode = "MarkdownV2",
            };
        }
        else
        {
            api = $"https://api.telegram.org/bot{channel.BotToken}/sendMessage";
            payload = new
            {
                chat_id = channel.ChatId,
                text,
                parse_mode = "MarkdownV2",
                disable_web_page_preview = true,
            };
        }

        try
        {
            using var resp = await _http.PostAsJsonAsync(api, payload, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "telegram send failed");
        }
    }
}
