using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace FlightJar.Clients.Vfrmap;

/// <summary>
/// Auto-discovers VFRMap.com's current FAA chart cycle date (YYYYMMDD).
/// Ports <c>app/vfrmap_cycle.py</c>. The client scrapes the homepage to find
/// <c>js/map.js</c>, then scrapes that for the embedded cycle date.
/// </summary>
public sealed partial class VfrmapCycle : IAsyncDisposable
{
    public const string IndexUrl = "https://vfrmap.com/";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    public const int CacheSchemaVersion = 1;

    private const int MinYear = 2010;
    private const int MaxYearsAhead = 1;

    [GeneratedRegex("""src=["']([^"']*?js/map\.js[^"']*?)["']""")]
    private static partial Regex MapJsRegex();

    [GeneratedRegex(@"(?<![0-9])(20\d{6})(?![0-9])")]
    private static partial Regex CycleRegex();

    private readonly HttpClient _http;
    private readonly ILogger<VfrmapCycle> _logger;
    private readonly TimeProvider _time;
    private readonly string? _cachePath;
    private readonly string _override;
    private string? _date;

    public VfrmapCycle(
        HttpClient http,
        ILogger<VfrmapCycle> logger,
        TimeProvider? time = null,
        string? cachePath = null,
        string? overrideDate = null)
    {
        _http = http;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _cachePath = cachePath;
        _override = (overrideDate ?? string.Empty).Trim();
        if (_override.Length > 0)
        {
            _date = _override;
        }
    }

    /// <summary>The currently known cycle date (YYYYMMDD), or null if unknown.</summary>
    public string? CurrentDate => _date;

    /// <summary>Load a previously-discovered cycle from disk. No network.</summary>
    public async Task LoadCacheAsync(CancellationToken ct = default)
    {
        if (_override.Length > 0 || _cachePath is null || !File.Exists(_cachePath))
        {
            return;
        }
        try
        {
            var raw = await File.ReadAllTextAsync(_cachePath, ct);
            var doc = JsonSerializer.Deserialize<CachePayload>(raw);
            if (doc?.SchemaVersion != CacheSchemaVersion || doc.Date is null)
            {
                return;
            }
            if (IsValidCycle(doc.Date))
            {
                _date = doc.Date;
                _logger.LogInformation("loaded cached VFRMap cycle {Cycle}", doc.Date);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to read VFRMap cycle cache");
        }
    }

    private async Task SaveCacheAsync(string cycle, CancellationToken ct)
    {
        if (_cachePath is null)
        {
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var payload = new CachePayload
            {
                SchemaVersion = CacheSchemaVersion,
                Date = cycle,
                DiscoveredAt = _time.GetUtcNow().ToUnixTimeSeconds(),
            };
            var tmp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(payload), ct);
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to write VFRMap cycle cache");
        }
    }

    internal static bool IsValidCycle(string cycle)
    {
        if (!DateTime.TryParseExact(cycle, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
        {
            return false;
        }
        var today = DateTime.UtcNow.Date;
        if (d.Year < MinYear)
        {
            return false;
        }
        return d.Year <= today.Year + MaxYearsAhead;
    }

    /// <summary>Extract the js/map.js path (with any cache-buster) from the homepage HTML.</summary>
    public static string? ExtractMapJsPath(string html)
    {
        var m = MapJsRegex().Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Pull the newest plausible YYYYMMDD cycle from map.js source.</summary>
    public static string? ExtractCycle(string js)
    {
        var matches = CycleRegex().Matches(js);
        string? best = null;
        foreach (Match m in matches)
        {
            var candidate = m.Groups[1].Value;
            if (IsValidCycle(candidate) && (best is null || string.CompareOrdinal(candidate, best) > 0))
            {
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>
    /// Scrape VFRMap for the current cycle. Returns the discovered date, or
    /// null on any failure. A failure doesn't clobber the previously known
    /// value — callers keep serving the last known-good.
    /// </summary>
    public async Task<string?> DiscoverAsync(CancellationToken ct = default)
    {
        if (_override.Length > 0)
        {
            return _override;
        }
        try
        {
            using var indexReq = new HttpRequestMessage(HttpMethod.Get, IndexUrl);
            using var indexResp = await _http.SendAsync(indexReq, ct);
            indexResp.EnsureSuccessStatusCode();
            var indexHtml = await indexResp.Content.ReadAsStringAsync(ct);
            var mapJsPath = ExtractMapJsPath(indexHtml);
            if (mapJsPath is null)
            {
                _logger.LogWarning("VFRMap homepage has no map.js reference");
                return null;
            }

            var mapJsUrl = new Uri(new Uri(IndexUrl), mapJsPath).AbsoluteUri;
            using var mapJsReq = new HttpRequestMessage(HttpMethod.Get, mapJsUrl);
            using var mapJsResp = await _http.SendAsync(mapJsReq, ct);
            mapJsResp.EnsureSuccessStatusCode();
            var js = await mapJsResp.Content.ReadAsStringAsync(ct);
            var cycle = ExtractCycle(js);
            if (cycle is null)
            {
                _logger.LogWarning("VFRMap map.js contained no recognisable cycle dates");
                return null;
            }
            if (cycle != _date)
            {
                _logger.LogInformation(
                    "VFRMap cycle {Cycle} (previous: {Previous})",
                    cycle, _date ?? "unknown");
            }
            _date = cycle;
            await SaveCacheAsync(cycle, ct);
            return cycle;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VFRMap cycle discovery failed");
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class CachePayload
    {
        public int SchemaVersion { get; set; }
        public string? Date { get; set; }
        public long DiscoveredAt { get; set; }
    }
}
