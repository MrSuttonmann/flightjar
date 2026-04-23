using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlightJar.Clients.Caching;
using Microsoft.Extensions.Logging;

namespace FlightJar.Clients.OpenAip;

/// <summary>
/// Airspace / obstacle / reporting-point lookups against
/// <c>api.core.openaip.net</c>. Cached on-disk at schema v1 and served to the
/// browser via bbox endpoints.
///
/// Cache strategy: the requested bbox is snapped outward to a 2° grid so
/// small pans within the same area always hit the cache. Each snapped bbox
/// maps to one fetch per feature kind (airspaces / obstacles / reporting
/// points). OpenAIP paginates at <see cref="PageSize"/> items per page, so
/// fetching a single snapped bbox can involve several HTTP calls — they
/// share the throttle and the union is written as one cache entry.
/// Positive TTL is 7 days because the underlying data only changes on the
/// AIRAC 28-day cycle.
/// </summary>
public sealed class OpenAipClient : IAsyncDisposable
{
    public const string BaseUrl = "https://api.core.openaip.net/api";
    public const string ApiKeyHeader = "x-openaip-api-key";
    // v2: BboxKey changed from (MinLat, MinLon, MaxLat, MaxLon) snapped
    // outer bbox to single 2° tile (MinLat, MinLon). On-disk v1 data is
    // discarded by LoadCacheAsync's version check.
    public const int CacheSchemaVersion = 2;
    public const int PageSize = 500;
    public const int MaxPagesPerRequest = 8;

    /// <summary>Coarse grid (in degrees) the requested bbox is snapped outward
    /// to. Larger = fewer tiles = fewer requests, but more wasted data on
    /// zoomed-in views. 2° trades well for a VFR-style map zoom range.</summary>
    public const double BboxGridDegrees = 2.0;

    public static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(6);
    public const int CacheMaxSize = 2_000;
    public static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(1.2);

    private readonly HttpClient _http;
    private readonly ILogger<OpenAipClient> _logger;
    private readonly TimeProvider _time;
    private readonly string? _cachePath;
    private readonly string? _apiKey;
    private readonly GzipJsonCache _diskCache;
    private readonly HttpThrottle _throttle;

    private readonly ConcurrentDictionary<BboxKey, CacheEntry<AirspaceList>> _airspaces = new();
    private readonly ConcurrentDictionary<BboxKey, CacheEntry<ObstacleList>> _obstacles = new();
    private readonly ConcurrentDictionary<BboxKey, CacheEntry<ReportingPointList>> _reportingPoints = new();

    private readonly ConcurrentDictionary<BboxKey, Task<IReadOnlyList<Airspace>>> _airspacesInflight = new();
    private readonly ConcurrentDictionary<BboxKey, Task<IReadOnlyList<Obstacle>>> _obstaclesInflight = new();
    private readonly ConcurrentDictionary<BboxKey, Task<IReadOnlyList<ReportingPoint>>> _reportingPointsInflight = new();

    public bool Enabled { get; }
    internal HttpThrottle Throttle => _throttle;
    internal int AirspacesCacheCount => _airspaces.Count;
    internal int ObstaclesCacheCount => _obstacles.Count;
    internal int ReportingPointsCacheCount => _reportingPoints.Count;

    public OpenAipClient(
        HttpClient http,
        ILogger<OpenAipClient> logger,
        TimeProvider? time = null,
        string? cachePath = null,
        string? apiKey = null)
    {
        _http = http;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _cachePath = cachePath;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _diskCache = new GzipJsonCache(logger);
        _throttle = new HttpThrottle(MinRequestInterval);
        Enabled = _apiKey is not null;
    }

    public static async Task<OpenAipClient> CreateAsync(
        HttpClient http,
        ILogger<OpenAipClient> logger,
        TimeProvider? time = null,
        string? cachePath = null,
        string? apiKey = null,
        CancellationToken ct = default)
    {
        var c = new OpenAipClient(http, logger, time, cachePath, apiKey);
        await c.LoadCacheAsync(ct);
        return c;
    }

    public Task<IReadOnlyList<Airspace>> GetAirspacesAsync(
        double minLat, double minLon, double maxLat, double maxLon, CancellationToken ct = default)
        => GetAsync(minLat, minLon, maxLat, maxLon, "airspaces", _airspaces, _airspacesInflight,
            ParseAirspace, ct);

    public Task<IReadOnlyList<Obstacle>> GetObstaclesAsync(
        double minLat, double minLon, double maxLat, double maxLon, CancellationToken ct = default)
        => GetAsync(minLat, minLon, maxLat, maxLon, "obstacles", _obstacles, _obstaclesInflight,
            ParseObstacle, ct);

    public Task<IReadOnlyList<ReportingPoint>> GetReportingPointsAsync(
        double minLat, double minLon, double maxLat, double maxLon, CancellationToken ct = default)
        => GetAsync(minLat, minLon, maxLat, maxLon, "reporting-points", _reportingPoints, _reportingPointsInflight,
            ParseReportingPoint, ct);

    private async Task<IReadOnlyList<T>> GetAsync<T, TList>(
        double minLat, double minLon, double maxLat, double maxLon,
        string endpoint,
        ConcurrentDictionary<BboxKey, CacheEntry<TList>> cache,
        ConcurrentDictionary<BboxKey, Task<IReadOnlyList<T>>> inflight,
        Func<JsonElement, T?> parseItem,
        CancellationToken ct)
        where TList : class, IFeatureList<T>, new()
    {
        if (!Enabled)
        {
            _logger.LogInformation(
                "openaip {Endpoint} skipped — OPENAIP_API_KEY not set", endpoint);
            return Array.Empty<T>();
        }
        var now = _time.GetUtcNow();
        var tiles = BboxKey.TilesForBbox(minLat, minLon, maxLat, maxLon).ToList();
        var accumulated = new List<T>();
        int hits = 0, fetched = 0, stale = 0;

        foreach (var tile in tiles)
        {
            if (ct.IsCancellationRequested) break;
            IReadOnlyList<T> tileItems;
            if (cache.TryGetValue(tile, out var entry) && entry.IsFresh(now))
            {
                tileItems = entry.Data?.Items ?? Array.Empty<T>();
                hits++;
            }
            else if (_throttle.IsInCooldown(now))
            {
                // Throttle in cooldown: serve whatever stale entry we have
                // for this tile (possibly empty) rather than blocking.
                tileItems = entry?.Data?.Items ?? Array.Empty<T>();
                stale++;
            }
            else
            {
                // Share in-flight fetches across concurrent callers that
                // want the same tile. OCE propagation still applies — the
                // ServeOpenAip handler swallows it uniformly.
                var task = inflight.GetOrAdd(tile,
                    k => FetchTileAsync(k, endpoint, cache, parseItem, ct));
                try { tileItems = await task; }
                finally { inflight.TryRemove(tile, out _); }
                fetched++;
            }
            accumulated.AddRange(tileItems);
        }

        var filtered = Filter(accumulated, minLat, minLon, maxLat, maxLon);
        _logger.LogInformation(
            "openaip {Endpoint} {Tiles} tiles ({Hits} hit, {Fetched} fetched, {Stale} stale), {Total} items, {Filtered} in bbox",
            endpoint, tiles.Count, hits, fetched, stale, accumulated.Count, filtered.Count);
        return filtered;
    }

    private async Task<IReadOnlyList<T>> FetchTileAsync<T, TList>(
        BboxKey key,
        string endpoint,
        ConcurrentDictionary<BboxKey, CacheEntry<TList>> cache,
        Func<JsonElement, T?> parseItem,
        CancellationToken ct)
        where TList : class, IFeatureList<T>, new()
    {
        var items = new List<T>();
        bool fetched = false;
        bool partial = false;
        CacheEntry<TList>? existing = cache.TryGetValue(key, out var e) ? e : null;
        try
        {
            for (int page = 1; page <= MaxPagesPerRequest; page++)
            {
                await using var _ = await _throttle.AcquireAsync(_time, ct);
                var now = _time.GetUtcNow();
                if (_throttle.IsInCooldown(now))
                {
                    partial = true;
                    break;
                }
                var url = BuildUrl(endpoint, key, page);
                _logger.LogInformation(
                    "openaip GET {Endpoint} page {Page} for {Key}", endpoint, page, key);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (_apiKey is not null)
                {
                    req.Headers.Add(ApiKeyHeader, _apiKey);
                }
                req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                using var resp = await _http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _throttle.RecordCooldown(HttpThrottle.ParseRetryAfter(resp, _time), _time);
                    _logger.LogWarning(
                        "openaip {Endpoint} 429 — cooling down until {Until:O}",
                        endpoint, _throttle.CooldownUntil);
                    partial = true;
                    break;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "openaip {Endpoint} HTTP {Status} for {Key}",
                        endpoint, (int)resp.StatusCode, key);
                    partial = true;
                    break;
                }
                using var doc = await JsonDocument.ParseAsync(
                    await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var root = doc.RootElement;
                if (!root.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    break;
                }
                foreach (var el in arr.EnumerateArray())
                {
                    var parsed = parseItem(el);
                    if (parsed is not null)
                    {
                        items.Add(parsed);
                    }
                }
                fetched = true;
                // Stop once we've consumed the last page.
                if (!root.TryGetProperty("nextPage", out var np) || np.ValueKind == JsonValueKind.Null)
                {
                    break;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "openaip {Endpoint} HTTP error for {Key}", endpoint, key);
            partial = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "openaip {Endpoint} fetch failed for {Key}", endpoint, key);
            partial = true;
        }

        if (!fetched)
        {
            // Nothing parsed at all (first page failed) — serve whatever
            // was previously cached rather than poisoning the cache with
            // an empty result.
            return existing?.Data?.Items ?? Array.Empty<T>();
        }
        // Partial success: keep the items we did get, but cache short so
        // the next call retries the missing pages rather than waiting a
        // week for the positive TTL to expire.
        var ttl = partial
            ? NegativeTtl
            : (items.Count > 0 ? PositiveTtl : NegativeTtl);
        var list = new TList { Items = items };
        cache[key] = new CacheEntry<TList>(list, _time.GetUtcNow().Add(ttl));
        Prune(cache);
        await PersistCacheAsync(ct);
        return items;
    }

    private static IReadOnlyList<T> Filter<T>(
        IReadOnlyList<T> items, double minLat, double minLon, double maxLat, double maxLon)
    {
        if (items.Count == 0)
        {
            return items;
        }
        var result = new List<T>(items.Count);
        foreach (var item in items)
        {
            if (FeatureInBbox(item, minLat, minLon, maxLat, maxLon))
            {
                result.Add(item);
            }
        }
        return result;
    }

    private static bool FeatureInBbox<T>(T item, double minLat, double minLon, double maxLat, double maxLon)
    {
        switch (item)
        {
            case Obstacle o:
                return o.Lat >= minLat && o.Lat <= maxLat && o.Lon >= minLon && o.Lon <= maxLon;
            case ReportingPoint rp:
                return rp.Lat >= minLat && rp.Lat <= maxLat && rp.Lon >= minLon && rp.Lon <= maxLon;
            case Airspace a:
                return GeometryIntersectsBbox(a.Geometry, minLat, minLon, maxLat, maxLon);
            default:
                return true;
        }
    }

    /// <summary>Rough bbox-intersection test for a GeoJSON Polygon / MultiPolygon.
    /// We only need to exclude items whose geometry lies entirely outside the
    /// view — the browser handles exact clipping. One coordinate inside the
    /// box is enough to keep the feature.</summary>
    private static bool GeometryIntersectsBbox(
        GeoJsonGeometry? g, double minLat, double minLon, double maxLat, double maxLon)
    {
        if (g?.Coordinates.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var minX = minLon; var maxX = maxLon;
        var minY = minLat; var maxY = maxLat;
        double gMinX = double.PositiveInfinity, gMaxX = double.NegativeInfinity;
        double gMinY = double.PositiveInfinity, gMaxY = double.NegativeInfinity;
        void VisitPoint(JsonElement pt)
        {
            if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2) return;
            var x = pt[0].GetDouble();
            var y = pt[1].GetDouble();
            if (x < gMinX) gMinX = x;
            if (x > gMaxX) gMaxX = x;
            if (y < gMinY) gMinY = y;
            if (y > gMaxY) gMaxY = y;
        }
        void VisitRings(JsonElement coords, int depth)
        {
            if (coords.ValueKind != JsonValueKind.Array) return;
            foreach (var e in coords.EnumerateArray())
            {
                if (depth <= 1 && e.ValueKind == JsonValueKind.Array
                    && e.GetArrayLength() >= 2
                    && e[0].ValueKind == JsonValueKind.Number)
                {
                    VisitPoint(e);
                }
                else
                {
                    VisitRings(e, depth + 1);
                }
            }
        }
        VisitRings(g.Coordinates, 0);
        if (double.IsInfinity(gMinX)) return false;
        return gMaxX >= minX && gMinX <= maxX && gMaxY >= minY && gMinY <= maxY;
    }

    private static string BuildUrl(string endpoint, BboxKey key, int page)
    {
        var sb = new StringBuilder(BaseUrl).Append('/').Append(endpoint).Append('?');
        sb.Append("bbox=")
          .Append(Fmt(key.MinLon)).Append(',')
          .Append(Fmt(key.MinLat)).Append(',')
          .Append(Fmt(key.MaxLon)).Append(',')
          .Append(Fmt(key.MaxLat));
        sb.Append("&limit=").Append(PageSize);
        sb.Append("&page=").Append(page);
        return sb.ToString();
    }

    private static string Fmt(double d) => d.ToString("0.#####", CultureInfo.InvariantCulture);

    private static void Prune<TList>(ConcurrentDictionary<BboxKey, CacheEntry<TList>> cache)
        where TList : class
    {
        if (cache.Count <= CacheMaxSize)
        {
            return;
        }
        var keep = cache
            .OrderByDescending(kv => kv.Value.ExpiresAt)
            .Take(CacheMaxSize)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        cache.Clear();
        foreach (var (k, v) in keep)
        {
            cache[k] = v;
        }
    }

    // ---- OpenAIP → local model parsers ----

    internal static Airspace? ParseAirspace(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var id = StringOrNull(el, "_id");
        if (id is null) return null;
        var name = StringOrNull(el, "name");
        var typeName = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number
            ? AirspaceTypeName(t.GetInt32()) : null;
        var klass = el.TryGetProperty("icaoClass", out var ic) && ic.ValueKind == JsonValueKind.Number
            ? IcaoClassName(ic.GetInt32()) : null;
        var (lowerFt, lowerDatum) = ReadLimit(el, "lowerLimit");
        var (upperFt, upperDatum) = ReadLimit(el, "upperLimit");
        var geom = ReadGeometry(el);
        return new Airspace(id, name, klass, typeName, lowerFt, lowerDatum, upperFt, upperDatum, geom);
    }

    internal static Obstacle? ParseObstacle(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var id = StringOrNull(el, "_id");
        if (id is null) return null;
        var geom = ReadGeometry(el);
        if (geom is null || !TryPointLatLon(geom, out var lat, out var lon)) return null;
        var name = StringOrNull(el, "name");
        var typeName = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number
            ? ObstacleTypeName(t.GetInt32()) : null;
        var (heightFt, _) = ReadLimit(el, "height");
        var (elevFt, _) = ReadLimit(el, "elevation");
        return new Obstacle(id, name, typeName, heightFt, elevFt, lat, lon);
    }

    internal static ReportingPoint? ParseReportingPoint(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var id = StringOrNull(el, "_id");
        if (id is null) return null;
        var geom = ReadGeometry(el);
        if (geom is null || !TryPointLatLon(geom, out var lat, out var lon)) return null;
        var name = StringOrNull(el, "name");
        var compulsory = el.TryGetProperty("compulsory", out var c) && c.ValueKind == JsonValueKind.True;
        return new ReportingPoint(id, name, compulsory, lat, lon);
    }

    private static bool TryPointLatLon(GeoJsonGeometry g, out double lat, out double lon)
    {
        lat = 0; lon = 0;
        if (!string.Equals(g.Type, "Point", StringComparison.OrdinalIgnoreCase)) return false;
        if (g.Coordinates.ValueKind != JsonValueKind.Array || g.Coordinates.GetArrayLength() < 2) return false;
        lon = g.Coordinates[0].GetDouble();
        lat = g.Coordinates[1].GetDouble();
        return true;
    }

    private static GeoJsonGeometry? ReadGeometry(JsonElement el)
    {
        if (!el.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var type = StringOrNull(g, "type");
        if (type is null) return null;
        if (!g.TryGetProperty("coordinates", out var coords)) return null;
        return new GeoJsonGeometry(type, coords.Clone());
    }

    /// <summary>Resolve a <c>{value, unit, referenceDatum}</c> object into a
    /// (feet, datum-string) pair. Unit 0=m → converted to feet; 1=ft → used
    /// directly; 6=FL → multiplied by 100 and tagged as "FL".</summary>
    private static (int? Ft, string? Datum) ReadLimit(JsonElement parent, string field)
    {
        if (!parent.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }
        if (!el.TryGetProperty("value", out var vEl) || vEl.ValueKind != JsonValueKind.Number)
        {
            return (null, null);
        }
        var value = vEl.GetInt32();
        var unit = el.TryGetProperty("unit", out var uEl) && uEl.ValueKind == JsonValueKind.Number
            ? uEl.GetInt32() : 1;
        var refDatum = el.TryGetProperty("referenceDatum", out var rEl) && rEl.ValueKind == JsonValueKind.Number
            ? rEl.GetInt32() : 1;

        int ft;
        switch (unit)
        {
            case 0: ft = (int)Math.Round(value * 3.28084); break;   // metres
            case 6: ft = value * 100; break;                         // flight level
            default: ft = value; break;                              // feet
        }
        string datum = unit == 6 ? "FL"
            : refDatum switch { 0 => "GND", 2 => "STD", _ => "MSL" };
        return (ft, datum);
    }

    private static string? StringOrNull(JsonElement parent, string field)
    {
        if (!parent.TryGetProperty(field, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    // ---- enum name tables ----

    private static string IcaoClassName(int code) => code switch
    {
        0 => "A",
        1 => "B",
        2 => "C",
        3 => "D",
        4 => "E",
        5 => "F",
        6 => "G",
        8 => "SUA",
        _ => "?",
    };

    private static string AirspaceTypeName(int code) => code switch
    {
        0 => "Other",
        1 => "Restricted",
        2 => "Danger",
        3 => "Prohibited",
        4 => "CTR",
        5 => "TMZ",
        6 => "RMZ",
        7 => "TMA",
        8 => "TRA",
        9 => "TSA",
        10 => "FIR",
        11 => "UIR",
        12 => "ADIZ",
        13 => "ATZ",
        14 => "MATZ",
        15 => "Airway",
        16 => "MTR",
        17 => "Alert",
        18 => "Warning",
        19 => "Protected",
        20 => "HTZ",
        21 => "Gliding",
        22 => "TRP",
        23 => "TIZ",
        24 => "TIA",
        25 => "MTA",
        26 => "CTA",
        27 => "ACC",
        28 => "Aerial Sporting",
        29 => "Low Overflight",
        30 => "MRT",
        31 => "TFR",
        32 => "VFR",
        33 => "FIS",
        34 => "LTA",
        35 => "UTA",
        36 => "MCTR",
        _ => "Other",
    };

    private static string ObstacleTypeName(int code) => code switch
    {
        1 => "Chimney",
        2 => "Building",
        3 => "Wind Turbine",
        4 => "Tower",
        _ => "Obstacle",
    };

    // ---- disk persistence ----

    public async Task LoadCacheAsync(CancellationToken ct = default)
    {
        if (_cachePath is null) return;
        var payload = await _diskCache.LoadAsync<OpenAipCachePayload>(_cachePath, JsonOpts, ct);
        if (payload is null || payload.Version != CacheSchemaVersion)
        {
            if (payload is not null)
            {
                _logger.LogInformation(
                    "openaip cache schema {Version} != {Expected} — starting fresh",
                    payload.Version, CacheSchemaVersion);
            }
            return;
        }
        LoadEntries(_airspaces, payload.Airspaces);
        LoadEntries(_obstacles, payload.Obstacles);
        LoadEntries(_reportingPoints, payload.ReportingPoints);
        _logger.LogInformation(
            "loaded openaip cache: {Airspaces} airspace + {Obstacles} obstacle + {Reporting} reporting-point bbox entries",
            _airspaces.Count, _obstacles.Count, _reportingPoints.Count);
    }

    private void LoadEntries<TList>(
        ConcurrentDictionary<BboxKey, CacheEntry<TList>> cache,
        Dictionary<string, CacheEntry<TList>>? entries)
        where TList : class
    {
        if (entries is null) return;
        var now = _time.GetUtcNow();
        foreach (var (k, v) in entries)
        {
            if (v.ExpiresAt <= now) continue;
            if (BboxKey.TryParse(k, out var key))
            {
                cache[key] = v;
            }
        }
    }

    private async Task PersistCacheAsync(CancellationToken ct)
    {
        if (_cachePath is null) return;
        var payload = new OpenAipCachePayload
        {
            Version = CacheSchemaVersion,
            Airspaces = _airspaces.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            Obstacles = _obstacles.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            ReportingPoints = _reportingPoints.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        };
        await _diskCache.SaveAsync(_cachePath, payload, JsonOpts, ct);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- internal container types ----

    /// <summary>Marker interface so the generic cache helpers can wrap a
    /// List&lt;T&gt; in a class (required by <see cref="CacheEntry{T}"/>'s
    /// <c>class</c> constraint).</summary>
    internal interface IFeatureList<T>
    {
        IReadOnlyList<T> Items { get; set; }
    }

    internal sealed class AirspaceList : IFeatureList<Airspace>
    {
        public IReadOnlyList<Airspace> Items { get; set; } = Array.Empty<Airspace>();
    }
    internal sealed class ObstacleList : IFeatureList<Obstacle>
    {
        public IReadOnlyList<Obstacle> Items { get; set; } = Array.Empty<Obstacle>();
    }
    internal sealed class ReportingPointList : IFeatureList<ReportingPoint>
    {
        public IReadOnlyList<ReportingPoint> Items { get; set; } = Array.Empty<ReportingPoint>();
    }

    internal sealed class OpenAipCachePayload
    {
        public int Version { get; set; }
        public Dictionary<string, CacheEntry<AirspaceList>>? Airspaces { get; set; }
        public Dictionary<string, CacheEntry<ObstacleList>>? Obstacles { get; set; }
        public Dictionary<string, CacheEntry<ReportingPointList>>? ReportingPoints { get; set; }
    }
}

/// <summary>
/// Cache key for a single <see cref="OpenAipClient.BboxGridDegrees"/>° tile.
/// The earlier design used one key per snapped <em>outer</em> bbox (a
/// request-shaped multi-tile rectangle), which turned out to miss the cache
/// every time a user pan nudged the viewport across a tile boundary — even
/// when every tile the new viewport touched was already cached under a
/// different outer-bbox key. Keying by individual tile instead lets two
/// different-shaped viewports that overlap the same tiles share cache
/// entries.
/// </summary>
public readonly record struct BboxKey(double MinLat, double MinLon)
{
    public double MaxLat => MinLat + OpenAipClient.BboxGridDegrees;
    public double MaxLon => MinLon + OpenAipClient.BboxGridDegrees;

    /// <summary>Enumerate every tile whose 2° cell intersects the requested
    /// bbox. Lat is clamped to world bounds; longitude wrapping across the
    /// antimeridian is not handled — requests that straddle it are
    /// vanishingly rare for our use case and filtered to the exact user
    /// bbox downstream anyway.</summary>
    public static IEnumerable<BboxKey> TilesForBbox(
        double minLat, double minLon, double maxLat, double maxLon)
    {
        var g = OpenAipClient.BboxGridDegrees;
        static double Floor(double v, double g) => Math.Floor(v / g) * g;
        static double Ceil(double v, double g) => Math.Ceiling(v / g) * g;
        var startLat = Math.Max(-90, Floor(minLat, g));
        var endLat = Math.Min(90, Ceil(maxLat, g));
        var startLon = Floor(minLon, g);
        var endLon = Ceil(maxLon, g);
        for (var lat = startLat; lat < endLat; lat += g)
        {
            for (var lon = startLon; lon < endLon; lon += g)
            {
                yield return new BboxKey(lat, lon);
            }
        }
    }

    public override string ToString()
        => FormattableString.Invariant($"{MinLat:0.###},{MinLon:0.###}");

    public static bool TryParse(string s, out BboxKey key)
    {
        key = default;
        var parts = s.Split(',');
        if (parts.Length != 2) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minLat)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minLon)) return false;
        key = new BboxKey(minLat, minLon);
        return true;
    }
}
