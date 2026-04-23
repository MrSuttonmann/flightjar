using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightJar.Clients.Caching;
using Microsoft.Extensions.Logging;

namespace FlightJar.Clients.Metar;

/// <summary>
/// Batched METAR lookups against aviationweather.gov. Ports <c>app/metar.py</c>.
/// One request per tick covers all airports in a single batch.
/// </summary>
public sealed class MetarClient : IAsyncDisposable
{
    public const string MetarUrl = "https://aviationweather.gov/api/data/metar";
    public const int CacheSchemaVersion = 1;

    public static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(5);
    public const int CacheMaxSize = 2_000;
    public static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Default429Cooldown = TimeSpan.FromSeconds(120);

    // Higher = cloudier, for picking the headline layer.
    private static readonly IReadOnlyDictionary<string, int> CoverRank = new Dictionary<string, int>
    {
        ["SKC"] = 0,
        ["CLR"] = 0,
        ["FEW"] = 1,
        ["SCT"] = 2,
        ["BKN"] = 3,
        ["OVC"] = 4,
    };

    private readonly HttpClient _http;
    private readonly ILogger<MetarClient> _logger;
    private readonly TimeProvider _time;
    private readonly string? _cachePath;
    private readonly GzipJsonCache _diskCache;
    private readonly ConcurrentDictionary<string, CacheEntry<MetarEntry>> _entries = new();
    private readonly ConcurrentDictionary<string, Task<IReadOnlyDictionary<string, MetarEntry?>>> _inflight = new();
    private readonly HttpThrottle _throttle;

    public bool Enabled { get; }
    internal HttpThrottle Throttle => _throttle;
    internal int Count => _entries.Count;

    public MetarClient(
        HttpClient http,
        ILogger<MetarClient> logger,
        TimeProvider? time = null,
        string? cachePath = null,
        bool enabled = true)
    {
        _http = http;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _cachePath = cachePath;
        _diskCache = new GzipJsonCache(logger);
        Enabled = enabled;
        _throttle = new HttpThrottle(MinRequestInterval, Default429Cooldown);
    }

    public static async Task<MetarClient> CreateAsync(
        HttpClient http,
        ILogger<MetarClient> logger,
        TimeProvider? time = null,
        string? cachePath = null,
        bool enabled = true,
        CancellationToken ct = default)
    {
        var c = new MetarClient(http, logger, time, cachePath, enabled);
        await c.LoadCacheAsync(ct);
        return c;
    }

    internal static string? NormaliseIcao(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }
        var k = icao.Trim().ToUpperInvariant();
        if (k.Length < 3 || k.Length > 4)
        {
            return null;
        }
        foreach (var c in k)
        {
            if (!char.IsLetterOrDigit(c))
            {
                return null;
            }
        }
        return k;
    }

    public (bool Known, MetarEntry? Data) LookupCached(string icao)
    {
        var key = NormaliseIcao(icao);
        if (key is null)
        {
            return (false, null);
        }
        if (_entries.TryGetValue(key, out var entry) && entry.IsFresh(_time.GetUtcNow()))
        {
            return (true, entry.Data);
        }
        return (false, null);
    }

    public async Task<MetarEntry?> LookupAsync(string icao, CancellationToken ct = default)
    {
        var key = NormaliseIcao(icao);
        if (key is null)
        {
            return null;
        }
        var many = await LookupManyAsync(new[] { icao }, ct);
        return many.TryGetValue(key, out var entry) ? entry : null;
    }

    public async Task<IReadOnlyDictionary<string, MetarEntry?>> LookupManyAsync(
        IEnumerable<string> codes, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return new Dictionary<string, MetarEntry?>();
        }

        var now = _time.GetUtcNow();
        var result = new Dictionary<string, MetarEntry?>(StringComparer.Ordinal);
        var fresh = new List<string>();

        foreach (var raw in codes)
        {
            var key = NormaliseIcao(raw);
            if (key is null || result.ContainsKey(key))
            {
                continue;
            }
            if (_entries.TryGetValue(key, out var entry) && entry.IsFresh(now))
            {
                result[key] = entry.Data;
            }
            else
            {
                fresh.Add(key);
            }
        }

        if (fresh.Count == 0)
        {
            return result;
        }

        if (_throttle.IsInCooldown(now))
        {
            foreach (var key in fresh)
            {
                _entries.TryGetValue(key, out var cached);
                result[key] = cached?.Data;
            }
            return result;
        }

        // Dedupe concurrent batches on exactly the same key set.
        var dedupKey = string.Join(",", fresh.OrderBy(k => k, StringComparer.Ordinal));
        var batchTask = _inflight.GetOrAdd(dedupKey, _ => FetchBatchAsync(fresh, ct));
        var batchResult = await batchTask;
        foreach (var key in fresh)
        {
            result[key] = batchResult.TryGetValue(key, out var v) ? v : null;
        }

        await PersistCacheAsync(ct);
        return result;
    }

    private async Task<IReadOnlyDictionary<string, MetarEntry?>> FetchBatchAsync(
        List<string> keys, CancellationToken ct)
    {
        try
        {
            IReadOnlyDictionary<string, MetarEntry>? payload = null;
            await using (await _throttle.AcquireAsync(_time, ct))
            {
                if (_throttle.IsInCooldown(_time.GetUtcNow()))
                {
                    return StaleFor(keys);
                }
                try
                {
                    payload = await FetchAsync(keys, ct);
                }
                catch (UpstreamRateLimitedException ex)
                {
                    _throttle.RecordCooldown(ex.RetryAfter, _time);
                    _logger.LogWarning(
                        "metar 429 — cooling down until {Until:O}", _throttle.CooldownUntil);
                    return StaleFor(keys);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "metar HTTP error");
                    return StaleFor(keys);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "metar fetch failed");
                    return StaleFor(keys);
                }
            }

            var now = _time.GetUtcNow();
            var batch = new Dictionary<string, MetarEntry?>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                payload!.TryGetValue(key, out var data);
                var ttl = data is not null ? PositiveTtl : NegativeTtl;
                _entries[key] = new CacheEntry<MetarEntry>(data, now.Add(ttl));
                batch[key] = data;
            }
            Prune();
            return batch;
        }
        finally
        {
            _inflight.TryRemove(string.Join(",", keys.OrderBy(k => k, StringComparer.Ordinal)), out _);
        }
    }

    private IReadOnlyDictionary<string, MetarEntry?> StaleFor(IEnumerable<string> keys)
    {
        var result = new Dictionary<string, MetarEntry?>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            _entries.TryGetValue(key, out var cached);
            result[key] = cached?.Data;
        }
        return result;
    }

    private async Task<IReadOnlyDictionary<string, MetarEntry>> FetchAsync(
        List<string> keys, CancellationToken ct)
    {
        var ids = string.Join(",", keys);
        var url = $"{MetarUrl}?ids={Uri.EscapeDataString(ids)}&format=json&taf=false&hours=1";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return new Dictionary<string, MetarEntry>();
        }
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new UpstreamRateLimitedException(HttpThrottle.ParseRetryAfter(resp, _time));
        }
        resp.EnsureSuccessStatusCode();

        var raw = await resp.Content.ReadAsStringAsync(ct);
        List<AviationWeatherEntry>? body;
        try
        {
            body = JsonSerializer.Deserialize<List<AviationWeatherEntry>>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "metar JSON parse failed");
            return new Dictionary<string, MetarEntry>();
        }
        var result = new Dictionary<string, MetarEntry>(StringComparer.Ordinal);
        if (body is null)
        {
            return result;
        }
        foreach (var entry in body)
        {
            var ident = (entry.IcaoId ?? entry.Icao)?.ToUpperInvariant();
            if (string.IsNullOrEmpty(ident))
            {
                continue;
            }
            var distilled = Distill(entry);
            if (distilled is not null)
            {
                result[ident] = distilled;
            }
        }
        return result;
    }

    private static MetarEntry? Distill(AviationWeatherEntry obs)
    {
        var raw = obs.RawOb;
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }
        return new MetarEntry
        {
            Raw = raw,
            ObsTime = obs.ObsTime,
            WindDir = obs.Wdir,
            WindKt = obs.Wspd,
            GustKt = obs.Wgst,
            Visibility = obs.Visib,
            TempC = obs.Temp,
            DewpointC = obs.Dewp,
            AltimeterHpa = obs.Altim,
            Cover = HeadlineCover(obs.Clouds),
        };
    }

    private static string? HeadlineCover(List<AviationWeatherCloud>? clouds)
    {
        if (clouds is null || clouds.Count == 0)
        {
            return null;
        }
        string? best = null;
        var bestRank = -1;
        foreach (var layer in clouds)
        {
            var code = layer.Cover?.ToUpperInvariant() ?? string.Empty;
            var rank = CoverRank.TryGetValue(code, out var r) ? r : -1;
            if (rank > bestRank)
            {
                bestRank = rank;
                best = code;
            }
        }
        return best;
    }

    public Task FlushAsync(CancellationToken ct = default) => PersistCacheAsync(ct);

    public async Task LoadCacheAsync(CancellationToken ct = default)
    {
        if (_cachePath is null)
        {
            return;
        }
        var payload = await _diskCache.LoadAsync<MetarCachePayload>(_cachePath, JsonOpts, ct);
        if (payload is null || payload.Version != CacheSchemaVersion)
        {
            if (payload is not null)
            {
                _logger.LogInformation(
                    "metar cache schema {Version} != {Expected} — starting fresh",
                    payload.Version, CacheSchemaVersion);
            }
            return;
        }
        var now = _time.GetUtcNow();
        foreach (var (k, v) in payload.Cache ?? new())
        {
            if (v.ExpiresAt > now)
            {
                _entries[k] = v;
            }
        }
        _logger.LogInformation("loaded {Count} metar cache entries", _entries.Count);
    }

    private async Task PersistCacheAsync(CancellationToken ct)
    {
        if (_cachePath is null)
        {
            return;
        }
        var payload = new MetarCachePayload
        {
            Version = CacheSchemaVersion,
            Cache = _entries.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        await _diskCache.SaveAsync(_cachePath, payload, JsonOpts, ct);
    }

    private void Prune()
    {
        if (_entries.Count <= CacheMaxSize)
        {
            return;
        }
        var keep = _entries
            .OrderByDescending(kv => kv.Value.ExpiresAt)
            .Take(CacheMaxSize)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        _entries.Clear();
        foreach (var (k, v) in keep)
        {
            _entries[k] = v;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class AviationWeatherEntry
    {
        public string? IcaoId { get; set; }
        public string? Icao { get; set; }
        public string? RawOb { get; set; }
        public long? ObsTime { get; set; }
        // wdir is "VRB" (string) for variable-wind stations, otherwise a
        // number — JsonElement passes whichever token came in.
        public System.Text.Json.JsonElement? Wdir { get; set; }
        public double? Wspd { get; set; }
        public double? Wgst { get; set; }
        public System.Text.Json.JsonElement? Visib { get; set; }
        public double? Temp { get; set; }
        public double? Dewp { get; set; }
        public double? Altim { get; set; }
        public List<AviationWeatherCloud>? Clouds { get; set; }
    }

    private sealed class AviationWeatherCloud
    {
        public string? Cover { get; set; }
    }

    internal sealed class MetarCachePayload
    {
        public int Version { get; set; }
        public Dictionary<string, CacheEntry<MetarEntry>>? Cache { get; set; }
    }
}
