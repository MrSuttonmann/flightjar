using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Persistence.Notifications;

/// <summary>
/// Persistent UI-managed notification channel configuration.
/// Validates on load + replace; unknown / malformed entries are silently
/// dropped. Wire format is v1 flat-shape, snake_case keys, discriminated
/// by the <c>type</c> field — see
/// <see cref="NotificationChannelJsonConverter"/>.
/// </summary>
public sealed class NotificationsConfigStore
{
    public const int SchemaVersion = 1;

    private readonly object _gate = new();
    private readonly List<NotificationChannel> _channels = new();
    private readonly string? _path;
    private readonly ILogger _logger;

    public NotificationsConfigStore(string? path = null, ILogger<NotificationsConfigStore>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<NotificationsConfigStore>.Instance;
    }

    public IReadOnlyList<NotificationChannel> Channels
    {
        get { lock (_gate) { return _channels.ToList(); } }
    }

    public NotificationChannel? Find(string id)
    {
        lock (_gate)
        {
            return _channels.FirstOrDefault(c => c.Id == id);
        }
    }

    /// <summary>Replace the channel list. Drops malformed entries. Persists
    /// only when the cleaned list differs from the current state.</summary>
    public IReadOnlyList<NotificationChannel> Replace(IEnumerable<NotificationChannel?> incoming)
    {
        var cleaned = new List<NotificationChannel>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in incoming)
        {
            var c = Clean(raw);
            if (c is null)
            {
                continue;
            }
            if (seenIds.Contains(c.Id))
            {
                c = c with { Id = GenerateId() };
            }
            seenIds.Add(c.Id);
            cleaned.Add(c);
        }

        bool changed;
        lock (_gate)
        {
            changed = !_channels.SequenceEqual(cleaned);
            if (changed)
            {
                _channels.Clear();
                _channels.AddRange(cleaned);
            }
        }
        if (changed)
        {
            Persist();
        }
        return Channels;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }
        try
        {
            var raw = await File.ReadAllTextAsync(_path, ct);
            var payload = JsonSerializer.Deserialize<PersistedPayload>(raw, NotificationsJson.Options);
            var channels = payload?.Channels ?? new List<NotificationChannel?>();
            lock (_gate)
            {
                _channels.Clear();
                foreach (var c in channels)
                {
                    var cleaned = Clean(c);
                    if (cleaned is not null)
                    {
                        _channels.Add(cleaned);
                    }
                }
            }
            _logger.LogInformation(
                "loaded {Count} notification channel(s) from {Path}", _channels.Count, _path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "notifications config unreadable at {Path}", _path);
        }
    }

    /// <summary>
    /// Normalise a freshly-deserialised channel: assign a missing id,
    /// fill in a default name, and trim per-type fields. Cross-type
    /// field stripping is intrinsic to the polymorphic model — each
    /// subclass only carries its own fields.
    /// </summary>
    private static NotificationChannel? Clean(NotificationChannel? raw)
    {
        if (raw is null)
        {
            return null;
        }
        var id = string.IsNullOrWhiteSpace(raw.Id) ? GenerateId() : raw.Id.Trim();
        var defaultName = raw.Type.ToString().ToLowerInvariant() + " channel";
        var name = string.IsNullOrWhiteSpace(raw.Name) ? defaultName : raw.Name.Trim();
        var common = raw with { Id = id, Name = name };

        return common switch
        {
            TelegramChannel tg => tg with
            {
                BotToken = (tg.BotToken ?? "").Trim(),
                ChatId = (tg.ChatId ?? "").Trim(),
            },
            NtfyChannel n => n with
            {
                Url = (n.Url ?? "").Trim(),
                Token = (n.Token ?? "").Trim(),
            },
            WebhookChannel w => w with
            {
                Url = (w.Url ?? "").Trim(),
            },
            _ => null,
        };
    }

    private static string GenerateId() => Guid.NewGuid().ToString("N")[..12];

    private void Persist()
    {
        if (_path is null)
        {
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            PersistedPayload payload;
            lock (_gate)
            {
                payload = new PersistedPayload
                {
                    Version = SchemaVersion,
                    Channels = _channels.Cast<NotificationChannel?>().ToList(),
                };
            }
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, NotificationsJson.Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "couldn't persist notifications config");
        }
    }

    private sealed class PersistedPayload
    {
        public int Version { get; set; }
        public List<NotificationChannel?>? Channels { get; set; }
    }
}
