using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightJar.Clients.Caching;
using Microsoft.Extensions.Logging;

namespace FlightJar.Clients.Planespotters;

/// <summary>
/// Aircraft photo lookups against planespotters.net. Ports <c>app/photos.py</c>.
/// Single bucket keyed by uppercased registration.
/// </summary>
public sealed class PlanespottersClient : IAsyncDisposable
{
    public const string PhotoUrl = "https://api.planespotters.net/pub/photos/reg/{0}";
    public const int CacheSchemaVersion = 1;

    public static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(30);
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromHours(24);
    public const int CacheMaxSize = 10_000;
    public static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(1.2);

    private readonly HttpClient _http;
    private readonly ILogger<PlanespottersClient> _logger;
    private readonly TimeProvider _time;
    private readonly string? _cachePath;
    private readonly GzipJsonCache _diskCache;
    private readonly CachedLookup<string, PhotoInfo> _cache;
    private readonly HttpThrottle _throttle;

    public bool Enabled { get; }
    internal HttpThrottle Throttle => _throttle;

    public PlanespottersClient(
        HttpClient http,
        ILogger<PlanespottersClient> logger,
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
        _throttle = new HttpThrottle(MinRequestInterval);
        _cache = new CachedLookup<string, PhotoInfo>(
            "planespotters", PositiveTtl, NegativeTtl, CacheMaxSize, _throttle, _time, logger);
    }

    public static async Task<PlanespottersClient> CreateAsync(
        HttpClient http,
        ILogger<PlanespottersClient> logger,
        TimeProvider? time = null,
        string? cachePath = null,
        bool enabled = true,
        CancellationToken ct = default)
    {
        var c = new PlanespottersClient(http, logger, time, cachePath, enabled);
        await c.LoadCacheAsync(ct);
        return c;
    }

    public (bool Known, PhotoInfo? Data) LookupCached(string registration)
    {
        var key = NormaliseRegistration(registration);
        return key is null ? (false, null) : _cache.LookupCached(key);
    }

    public async Task<PhotoInfo?> LookupAsync(string registration, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return null;
        }
        var key = NormaliseRegistration(registration);
        if (key is null)
        {
            return null;
        }
        var result = await _cache.GetAsync(key, FetchAsync, ct);
        await PersistCacheAsync(ct);
        return result;
    }

    internal static string? NormaliseRegistration(string? reg)
    {
        if (string.IsNullOrWhiteSpace(reg))
        {
            return null;
        }
        var k = reg.Trim().ToUpperInvariant();
        return k.Length == 0 ? null : k;
    }

    private async ValueTask<PhotoInfo?> FetchAsync(string reg, CancellationToken ct)
    {
        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, PhotoUrl, reg);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new UpstreamRateLimitedException(HttpThrottle.ParseRetryAfter(resp, _time));
        }
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<PlanespottersEnvelope>(JsonOpts, ct);
        var photos = body?.Photos;
        if (photos is null || photos.Count == 0)
        {
            return null;
        }
        var p = photos[0];
        var thumb = p.Thumbnail?.Src;
        var large = p.ThumbnailLarge?.Src ?? thumb;
        if (string.IsNullOrEmpty(thumb))
        {
            return null;
        }
        return new PhotoInfo(
            Thumbnail: thumb,
            Large: string.IsNullOrEmpty(large) ? null : large,
            Link: string.IsNullOrEmpty(p.Link) ? null : p.Link,
            Photographer: string.IsNullOrEmpty(p.Photographer) ? null : p.Photographer);
    }

    public Task FlushAsync(CancellationToken ct = default) => PersistCacheAsync(ct);

    public async Task LoadCacheAsync(CancellationToken ct = default)
    {
        if (_cachePath is null)
        {
            return;
        }
        var payload = await _diskCache.LoadAsync<PlanespottersCachePayload>(_cachePath, JsonOpts, ct);
        if (payload is null || payload.Version != CacheSchemaVersion)
        {
            if (payload is not null)
            {
                _logger.LogInformation(
                    "planespotters cache schema {Version} != {Expected} — starting fresh",
                    payload.Version, CacheSchemaVersion);
            }
            return;
        }
        _cache.LoadEntries(payload.Cache ?? new Dictionary<string, CacheEntry<PhotoInfo>>());
        _logger.LogInformation("loaded {Count} planespotters photo cache entries", _cache.Count);
    }

    private async Task PersistCacheAsync(CancellationToken ct)
    {
        if (_cachePath is null)
        {
            return;
        }
        var payload = new PlanespottersCachePayload
        {
            Version = CacheSchemaVersion,
            Cache = _cache.SnapshotEntries().ToDictionary(kv => kv.Key, kv => kv.Value),
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

    private sealed class PlanespottersEnvelope
    {
        public List<PlanespottersPhoto>? Photos { get; set; }
    }

    private sealed class PlanespottersPhoto
    {
        public PlanespottersPhotoSrc? Thumbnail { get; set; }
        public PlanespottersPhotoSrc? ThumbnailLarge { get; set; }
        public string? Link { get; set; }
        public string? Photographer { get; set; }
    }

    private sealed class PlanespottersPhotoSrc
    {
        public string? Src { get; set; }
    }

    internal sealed class PlanespottersCachePayload
    {
        public int Version { get; set; }
        public Dictionary<string, CacheEntry<PhotoInfo>>? Cache { get; set; }
    }
}
