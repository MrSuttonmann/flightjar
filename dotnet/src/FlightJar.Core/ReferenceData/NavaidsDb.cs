using System.Collections.Frozen;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Core.ReferenceData;

/// <summary>
/// Navaid identifier → coordinates + type. Reads the
/// <a href="https://github.com/davidmegginson/ourairports-data">OurAirports</a>
/// navaids CSV. Ports <c>app/navaids_db.py</c>.
/// </summary>
public sealed class NavaidsDb
{
    private static readonly FrozenSet<string> KeepTypes = new[]
    {
        "VOR", "VOR-DME", "VORTAC", "NDB", "NDB-DME", "DME", "TACAN",
    }.ToFrozenSet();

    private static readonly IReadOnlyDictionary<string, int> TypeRank = new Dictionary<string, int>
    {
        ["VORTAC"] = 0,
        ["VOR-DME"] = 0,
        ["VOR"] = 1,
        ["DME"] = 2,
        ["TACAN"] = 2,
        ["NDB-DME"] = 3,
        ["NDB"] = 3,
    };

    private readonly ILogger _logger;
    private List<NavaidRecord> _rows = new();

    public NavaidsDb(ILogger<NavaidsDb>? logger = null)
    {
        _logger = logger ?? NullLogger<NavaidsDb>.Instance;
    }

    public int Count => _rows.Count;

    public async Task<int> LoadFromAsync(string path, CancellationToken ct = default)
    {
        using var reader = File.OpenText(path);
        var fresh = new List<NavaidRecord>();
        await foreach (var row in CsvReader.ReadDictAsync(reader, ',', ct))
        {
            var type = Trim(row, "type").ToUpperInvariant();
            if (!KeepTypes.Contains(type))
            {
                continue;
            }
            var ident = Trim(row, "ident").ToUpperInvariant();
            if (ident.Length == 0)
            {
                continue;
            }
            if (!double.TryParse(Trim(row, "latitude_deg"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(Trim(row, "longitude_deg"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                continue;
            }
            double? freqKhz = null;
            var freqRaw = Trim(row, "frequency_khz");
            if (freqRaw.Length > 0
                && double.TryParse(freqRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var freq))
            {
                freqKhz = freq;
            }
            fresh.Add(new NavaidRecord(
                Ident: ident,
                Name: Trim(row, "name"),
                Type: type,
                Lat: lat,
                Lon: lon,
                FrequencyKhz: freqKhz,
                Country: Trim(row, "iso_country"),
                AssociatedAirport: Trim(row, "associated_airport")));
        }
        _rows = fresh;
        return _rows.Count;
    }

    public List<NavaidRecord> Bbox(
        double minLat, double minLon, double maxLat, double maxLon, int limit = 2000)
    {
        var wraps = minLon > maxLon;
        var hits = new List<NavaidRecord>();
        foreach (var r in _rows)
        {
            if (r.Lat < minLat || r.Lat > maxLat)
            {
                continue;
            }
            var inLon = wraps ? (r.Lon >= minLon || r.Lon <= maxLon) : (r.Lon >= minLon && r.Lon <= maxLon);
            if (!inLon)
            {
                continue;
            }
            hits.Add(r);
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
                _logger.LogInformation("loaded navaids DB from {Path} ({Count} entries)", p, n);
                return n;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "failed to load navaids DB from {Path}", p);
            }
        }
        _logger.LogInformation("no navaids DB found; overlay disabled");
        return 0;
    }

    private static string Trim(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out var v) ? v.Trim() : "";
}

public sealed record NavaidRecord(
    string Ident,
    string Name,
    string Type,
    double Lat,
    double Lon,
    double? FrequencyKhz,
    string Country,
    string AssociatedAirport);
