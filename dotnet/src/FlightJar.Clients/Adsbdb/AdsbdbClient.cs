using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightJar.Clients.Caching;
using Microsoft.Extensions.Logging;

namespace FlightJar.Clients.Adsbdb;

/// <summary>
/// Route + aircraft lookups against adsbdb.com. Ports <c>app/flight_routes.py</c>:
/// two separate caches (routes by callsign, aircraft by ICAO24 hex) sharing a
/// single throttle and a single on-disk persistence file.
/// </summary>
public sealed class AdsbdbClient : IAsyncDisposable
{
    public const string CallsignUrl = "https://api.adsbdb.com/v0/callsign/{0}";
    public const string AircraftUrl = "https://api.adsbdb.com/v0/aircraft/{0}";
    public const int CacheSchemaVersion = 3;

    public static readonly TimeSpan RoutePositiveTtl = TimeSpan.FromHours(12);
    public static readonly TimeSpan RouteNegativeTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan AircraftPositiveTtl = TimeSpan.FromDays(30);
    public static readonly TimeSpan AircraftNegativeTtl = TimeSpan.FromHours(24);
    public const int CacheMaxSize = 10_000;
    public static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(1.2);

    private readonly HttpClient _http;
    private readonly ILogger<AdsbdbClient> _logger;
    private readonly TimeProvider _time;
    private readonly string? _cachePath;
    private readonly GzipJsonCache _diskCache;
    private readonly CachedLookup<string, RouteInfo> _routes;
    private readonly CachedLookup<string, AircraftRecord> _aircraft;
    private readonly HttpThrottle _throttle;

    public bool Enabled { get; }

    public AdsbdbClient(
        HttpClient http,
        ILogger<AdsbdbClient> logger,
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

        var throttle = new HttpThrottle(MinRequestInterval);
        _throttle = throttle;
        _routes = new CachedLookup<string, RouteInfo>(
            "adsbdb.routes", RoutePositiveTtl, RouteNegativeTtl, CacheMaxSize, throttle, _time, logger);
        _aircraft = new CachedLookup<string, AircraftRecord>(
            "adsbdb.aircraft", AircraftPositiveTtl, AircraftNegativeTtl, CacheMaxSize, throttle, _time, logger);
    }

    /// <summary>Returns the throttle state — primarily for tests asserting cooldown behaviour.</summary>
    internal HttpThrottle Throttle => _throttle;

    /// <summary>Static factory that constructs + loads the on-disk cache.</summary>
    public static async Task<AdsbdbClient> CreateAsync(
        HttpClient http,
        ILogger<AdsbdbClient> logger,
        TimeProvider? time = null,
        string? cachePath = null,
        bool enabled = true,
        CancellationToken ct = default)
    {
        var client = new AdsbdbClient(http, logger, time, cachePath, enabled);
        await client.LoadCacheAsync(ct);
        return client;
    }

    // ---- public API: routes ----

    public (bool Known, RouteInfo? Data) LookupCachedRoute(string callsign)
    {
        var key = NormaliseCallsign(callsign);
        return key is null ? (false, null) : _routes.LookupCached(key);
    }

    public async Task<RouteInfo?> LookupRouteAsync(string callsign, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return null;
        }
        var key = NormaliseCallsign(callsign);
        if (key is null)
        {
            return null;
        }
        var result = await _routes.GetAsync(key, FetchRouteAsync, ct);
        await PersistCacheAsync(ct);
        return result;
    }

    // ---- public API: aircraft ----

    public (bool Known, AircraftRecord? Data) LookupCachedAircraft(string icao)
    {
        var key = NormaliseIcao(icao);
        return key is null ? (false, null) : _aircraft.LookupCached(key);
    }

    public async Task<AircraftRecord?> LookupAircraftAsync(string icao, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return null;
        }
        var key = NormaliseIcao(icao);
        if (key is null)
        {
            return null;
        }
        var result = await _aircraft.GetAsync(key, FetchAircraftAsync, ct);
        await PersistCacheAsync(ct);
        return result;
    }

    // ---- key normalisation ----

    internal static string? NormaliseCallsign(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return null;
        }
        var k = callsign.Trim().ToUpperInvariant();
        // adsbdb returns 400 on callsigns shorter than 3 chars (partial
        // transmissions like "ID" before the flight-number suffix lands).
        // Anything alphanumeric-only is accepted — adsbdb also handles
        // N-numbers and country registrations beyond the airline ICAO set.
        if (k.Length < 3 || k.Length > 8)
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

    public static string? NormaliseIcao(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            return null;
        }
        var k = icao.Trim().ToLowerInvariant();
        if (k.Length == 0 || k.Length > 6)
        {
            return null;
        }
        foreach (var c in k)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return null;
            }
        }
        return k;
    }

    // ---- fetchers ----

    private async ValueTask<RouteInfo?> FetchRouteAsync(string callsign, CancellationToken ct)
    {
        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, CallsignUrl, callsign);
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
        var body = await resp.Content.ReadFromJsonAsync<AdsbdbEnvelope<AdsbdbRouteResponse>>(JsonOpts, ct);
        var route = body?.Response?.Flightroute;
        if (route is null)
        {
            return null;
        }
        var origin = route.Origin?.IcaoCode;
        var destination = route.Destination?.IcaoCode;
        if (string.IsNullOrEmpty(origin) && string.IsNullOrEmpty(destination))
        {
            return null;
        }
        return new RouteInfo(
            Origin: string.IsNullOrEmpty(origin) ? null : origin,
            Destination: string.IsNullOrEmpty(destination) ? null : destination,
            Callsign: string.IsNullOrEmpty(route.Callsign) ? callsign : route.Callsign);
    }

    private async ValueTask<AircraftRecord?> FetchAircraftAsync(string icao, CancellationToken ct)
    {
        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, AircraftUrl, icao);
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
        var body = await resp.Content.ReadFromJsonAsync<AdsbdbEnvelope<AdsbdbAircraftResponse>>(JsonOpts, ct);
        var ac = body?.Response?.Aircraft;
        if (ac is null)
        {
            return null;
        }
        return new AircraftRecord
        {
            Registration = NullIfEmpty(ac.Registration),
            Type = NullIfEmpty(ac.Type),
            IcaoType = NullIfEmpty(ac.IcaoType),
            Manufacturer = NullIfEmpty(ac.Manufacturer),
            Operator = NullIfEmpty(ac.RegisteredOwner),
            OperatorCountry = NullIfEmpty(ac.RegisteredOwnerCountryName),
            OperatorCountryIso = NullIfEmpty(ac.RegisteredOwnerCountryIsoName),
            PhotoUrl = NullIfEmpty(ac.UrlPhoto),
            PhotoThumbnail = NullIfEmpty(ac.UrlPhotoThumbnail),
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ---- disk persistence ----

    /// <summary>Block until any pending cache writes have flushed. Phase 2
    /// writes inline in Lookup*Async, so this currently just completes —
    /// reserved for the later move to batched/async persistence.</summary>
    public Task FlushAsync(CancellationToken ct = default) => PersistCacheAsync(ct);

    public async Task LoadCacheAsync(CancellationToken ct = default)
    {
        if (_cachePath is null)
        {
            return;
        }
        var payload = await _diskCache.LoadAsync<AdsbdbCachePayload>(_cachePath, JsonOpts, ct);
        if (payload is null || payload.Version != CacheSchemaVersion)
        {
            if (payload is not null)
            {
                _logger.LogInformation(
                    "adsbdb cache schema {Version} != {Expected} — starting fresh",
                    payload.Version, CacheSchemaVersion);
            }
            return;
        }
        _routes.LoadEntries(payload.Routes ?? new Dictionary<string, CacheEntry<RouteInfo>>());
        _aircraft.LoadEntries(payload.Aircraft ?? new Dictionary<string, CacheEntry<AircraftRecord>>());
        _logger.LogInformation(
            "loaded {Routes} route + {Aircraft} aircraft cache entries",
            _routes.Count, _aircraft.Count);
    }

    private async Task PersistCacheAsync(CancellationToken ct)
    {
        if (_cachePath is null)
        {
            return;
        }
        var payload = new AdsbdbCachePayload
        {
            Version = CacheSchemaVersion,
            Routes = _routes.SnapshotEntries().ToDictionary(kv => kv.Key, kv => kv.Value),
            Aircraft = _aircraft.SnapshotEntries().ToDictionary(kv => kv.Key, kv => kv.Value),
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

    // ---- JSON envelopes ----

    private sealed class AdsbdbEnvelope<T>
    {
        public T? Response { get; set; }
    }

    private sealed class AdsbdbRouteResponse
    {
        public AdsbdbRoute? Flightroute { get; set; }
    }

    private sealed class AdsbdbRoute
    {
        public string? Callsign { get; set; }
        public AdsbdbAirport? Origin { get; set; }
        public AdsbdbAirport? Destination { get; set; }
    }

    private sealed class AdsbdbAirport
    {
        public string? IcaoCode { get; set; }
    }

    private sealed class AdsbdbAircraftResponse
    {
        public AdsbdbAircraft? Aircraft { get; set; }
    }

    private sealed class AdsbdbAircraft
    {
        public string? Registration { get; set; }
        public string? Type { get; set; }
        public string? IcaoType { get; set; }
        public string? Manufacturer { get; set; }
        public string? RegisteredOwner { get; set; }
        public string? RegisteredOwnerCountryName { get; set; }
        public string? RegisteredOwnerCountryIsoName { get; set; }
        public string? UrlPhoto { get; set; }
        public string? UrlPhotoThumbnail { get; set; }
    }

    internal sealed class AdsbdbCachePayload
    {
        public int Version { get; set; }
        public Dictionary<string, CacheEntry<RouteInfo>>? Routes { get; set; }
        public Dictionary<string, CacheEntry<AircraftRecord>>? Aircraft { get; set; }
    }
}
