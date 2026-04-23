using System.Collections.Frozen;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Core.ReferenceData;

/// <summary>
/// ICAO airport code → human-readable name / location. Reads the
/// <a href="https://github.com/davidmegginson/ourairports-data">OurAirports</a>
/// public-domain CSV at startup. Ports <c>app/airports_db.py</c>.
/// </summary>
public sealed class AirportsDb
{
    /// <summary>Types worth keeping. Heliports, closed, seaplane bases clutter
    /// the display without adding value for ADS-B route lookups.</summary>
    private static readonly FrozenSet<string> KeepTypes = new[]
    {
        "small_airport", "medium_airport", "large_airport",
    }.ToFrozenSet();

    private static readonly IReadOnlyDictionary<string, int> TypeRank = new Dictionary<string, int>
    {
        ["large_airport"] = 0,
        ["medium_airport"] = 1,
        ["small_airport"] = 2,
    };

    private readonly ILogger _logger;
    private FrozenDictionary<string, AirportRecord> _byIcao = FrozenDictionary<string, AirportRecord>.Empty;

    public AirportsDb(ILogger<AirportsDb>? logger = null)
    {
        _logger = logger ?? NullLogger<AirportsDb>.Instance;
    }

    public int Count => _byIcao.Count;

    public AirportRecord? Lookup(string? icao)
    {
        if (string.IsNullOrEmpty(icao))
        {
            return null;
        }
        return _byIcao.TryGetValue(icao.ToUpperInvariant(), out var r) ? r : null;
    }

    public async Task<int> LoadFromAsync(string path, CancellationToken ct = default)
    {
        using var reader = File.OpenText(path);
        var fresh = new Dictionary<string, AirportRecord>(StringComparer.Ordinal);
        await foreach (var row in CsvReader.ReadDictAsync(reader, ',', ct))
        {
            if (!row.TryGetValue("type", out var type) || !KeepTypes.Contains(type))
            {
                continue;
            }
            var ident = Trim(row, "ident").ToUpperInvariant();
            var name = Trim(row, "name");
            if (ident.Length == 0 || name.Length == 0)
            {
                continue;
            }
            if (!double.TryParse(Trim(row, "latitude_deg"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(Trim(row, "longitude_deg"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                continue;
            }
            fresh[ident] = new AirportRecord(
                Icao: ident,
                Name: name,
                City: Trim(row, "municipality"),
                Country: Trim(row, "iso_country"),
                Type: type,
                Lat: lat,
                Lon: lon);
        }
        _byIcao = fresh.ToFrozenDictionary(StringComparer.Ordinal);
        return _byIcao.Count;
    }

    /// <summary>Return airports inside the given bounding box, biggest-first.
    /// Handles antimeridian wrap when <paramref name="minLon"/> &gt; <paramref name="maxLon"/>.</summary>
    public List<AirportRecord> Bbox(
        double minLat, double minLon, double maxLat, double maxLon, int limit = 2000)
    {
        var wraps = minLon > maxLon;
        var hits = new List<AirportRecord>();
        foreach (var info in _byIcao.Values)
        {
            if (info.Lat < minLat || info.Lat > maxLat)
            {
                continue;
            }
            var inLon = wraps ? (info.Lon >= minLon || info.Lon <= maxLon) : (info.Lon >= minLon && info.Lon <= maxLon);
            if (!inLon)
            {
                continue;
            }
            hits.Add(info);
        }
        hits.Sort((a, b) =>
        {
            var ra = TypeRank.TryGetValue(a.Type, out var x) ? x : 9;
            var rb = TypeRank.TryGetValue(b.Type, out var y) ? y : 9;
            return ra.CompareTo(rb);
        });
        if (hits.Count > limit)
        {
            hits.RemoveRange(limit, hits.Count - limit);
        }
        return hits;
    }

    public async Task<int> LoadFirstAvailableAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        foreach (var p in paths)
        {
            if (!File.Exists(p))
            {
                continue;
            }
            try
            {
                var n = await LoadFromAsync(p, ct);
                _logger.LogInformation("loaded airports DB from {Path} ({Count} entries)", p, n);
                return n;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "failed to load airports DB from {Path}", p);
            }
        }
        _logger.LogInformation("no airports DB found; tooltips disabled");
        return 0;
    }

    private static string Trim(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out var v) ? v.Trim() : "";
}

public sealed record AirportRecord(
    string Icao,
    string Name,
    string City,
    string Country,
    string Type,
    double Lat,
    double Lon);
