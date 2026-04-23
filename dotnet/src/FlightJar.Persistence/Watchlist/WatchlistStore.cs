using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Persistence.Watchlist;

/// <summary>
/// Persistent watchlist of ICAO24 hex codes plus a last-seen timestamp map.
/// Ports <c>app/watchlist.py</c>. Mirrored from the browser via the
/// <c>/api/watchlist</c> endpoints. Writes are debounced so a watchlisted
/// plane in coverage doesn't cause a disk rewrite every second.
/// </summary>
public sealed partial class WatchlistStore
{
    public const int CacheSchemaVersion = 2;
    public static readonly TimeSpan PersistDebounce = TimeSpan.FromSeconds(30);

    [GeneratedRegex("^[0-9a-f]{6}$")]
    private static partial Regex HexRegex();

    private readonly object _gate = new();
    private readonly HashSet<string> _set = new();
    private readonly ConcurrentDictionary<string, double> _lastSeen = new();
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private DateTimeOffset _lastPersist = DateTimeOffset.MinValue;

    public WatchlistStore(string? path = null, TimeProvider? time = null, ILogger<WatchlistStore>? logger = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<WatchlistStore>.Instance;
    }

    public int Count
    {
        get { lock (_gate) { return _set.Count; } }
    }

    public bool Contains(string icao)
    {
        var key = Normalise(icao);
        if (key is null) return false;
        lock (_gate) { return _set.Contains(key); }
    }

    public IReadOnlyList<string> Icao24s
    {
        get
        {
            lock (_gate)
            {
                var list = _set.ToList();
                list.Sort(StringComparer.Ordinal);
                return list;
            }
        }
    }

    public WatchlistPayload Snapshot()
    {
        lock (_gate)
        {
            var list = _set.ToList();
            list.Sort(StringComparer.Ordinal);
            return new WatchlistPayload(list, new Dictionary<string, double>(_lastSeen));
        }
    }

    /// <summary>Atomically swap the watchlist contents. Invalid entries are
    /// dropped; last-seen entries for removed icaos are pruned.</summary>
    public WatchlistPayload Replace(IEnumerable<string> icao24s)
    {
        var incoming = new HashSet<string>();
        foreach (var raw in icao24s)
        {
            var key = Normalise(raw);
            if (key is not null)
            {
                incoming.Add(key);
            }
        }

        lock (_gate)
        {
            var prunedLastSeen = _lastSeen
                .Where(kv => incoming.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var changed = !_set.SetEquals(incoming) || !DictEquals(prunedLastSeen, _lastSeen);
            if (!changed)
            {
                return Snapshot();
            }

            _set.Clear();
            foreach (var k in incoming)
            {
                _set.Add(k);
            }
            _lastSeen.Clear();
            foreach (var (k, v) in prunedLastSeen)
            {
                _lastSeen[k] = v;
            }
        }
        Persist();
        return Snapshot();
    }

    /// <summary>Record a sighting for a watchlisted icao. Ignores non-watched
    /// tails, zero/negative timestamps, and time-travel (older ts than stored).
    /// First sighting persists immediately; later ones debounce.</summary>
    public void RecordSeen(string icao, double ts)
    {
        var key = Normalise(icao);
        if (key is null || ts <= 0)
        {
            return;
        }
        bool watched;
        double? previous;
        lock (_gate)
        {
            watched = _set.Contains(key);
            previous = _lastSeen.TryGetValue(key, out var v) ? v : null;
        }
        if (!watched)
        {
            return;
        }
        if (previous is double p && ts <= p)
        {
            return;
        }
        _lastSeen[key] = ts;
        var now = _time.GetUtcNow();
        var isNew = previous is null;
        if (isNew || now - _lastPersist >= PersistDebounce)
        {
            Persist();
        }
    }

    /// <summary>Force-persist pending updates. Call on graceful shutdown.</summary>
    public void Flush() => Persist();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }
        try
        {
            var raw = await File.ReadAllTextAsync(_path, ct);
            var doc = JsonSerializer.Deserialize<PersistedPayload>(raw, JsonOpts);
            if (doc?.Icao24s is null)
            {
                return;
            }
            lock (_gate)
            {
                _set.Clear();
                foreach (var x in doc.Icao24s)
                {
                    var key = Normalise(x);
                    if (key is not null)
                    {
                        _set.Add(key);
                    }
                }
                _lastSeen.Clear();
                if (doc.LastSeen is not null)
                {
                    foreach (var (rawKey, ts) in doc.LastSeen)
                    {
                        var key = Normalise(rawKey);
                        if (key is null || !_set.Contains(key) || ts <= 0)
                        {
                            continue;
                        }
                        _lastSeen[key] = ts;
                    }
                }
            }
            _logger.LogInformation(
                "loaded {Count} watchlist entries ({Seen} with last-seen) from {Path}",
                _set.Count, _lastSeen.Count, _path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "watchlist store unreadable at {Path}", _path);
        }
    }

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
                var list = _set.ToList();
                list.Sort(StringComparer.Ordinal);
                payload = new PersistedPayload
                {
                    Version = CacheSchemaVersion,
                    Icao24s = list,
                    LastSeen = new Dictionary<string, double>(_lastSeen),
                };
            }
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
            _lastPersist = _time.GetUtcNow();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "couldn't persist watchlist to {Path}", _path);
        }
    }

    internal static string? Normalise(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }
        var k = icao.Trim().ToLowerInvariant();
        return HexRegex().IsMatch(k) ? k : null;
    }

    private static bool DictEquals(
        IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var other) || other != v) return false;
        }
        return true;
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class PersistedPayload
    {
        public int Version { get; set; }
        public List<string>? Icao24s { get; set; }
        public Dictionary<string, double>? LastSeen { get; set; }
    }
}

public sealed record WatchlistPayload(
    IReadOnlyList<string> Icao24s,
    IReadOnlyDictionary<string, double> LastSeen);
