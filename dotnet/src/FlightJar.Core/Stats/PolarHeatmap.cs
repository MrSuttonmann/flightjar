using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Core.Stats;

/// <summary>
/// Per-(bearing × distance) position-fix density tracker over a 7-day
/// rolling window. Mirrors <c>app/polar_heatmap.py</c>. 36 bearing
/// buckets (10°) × 12 distance bands (25 km each, capped at 300 km —
/// anything beyond that lands in the outermost band).
/// </summary>
public sealed class PolarHeatmap
{
    public const int Buckets = PolarCoverage.Buckets;
    public const double BucketDeg = PolarCoverage.BucketDeg;
    public const double BandKm = 25.0;
    public const int Bands = 12;
    public const int WindowDays = 7;

    private readonly object _gate = new();
    private readonly Dictionary<int, int[,]> _dailyGrids = new();
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private bool _dirty;
    private DateTimeOffset _lastPersist = DateTimeOffset.MinValue;

    public double? ReceiverLat { get; set; }
    public double? ReceiverLon { get; set; }

    public PolarHeatmap(
        double? receiverLat = null, double? receiverLon = null,
        string? cachePath = null, TimeProvider? time = null,
        ILogger<PolarHeatmap>? logger = null)
    {
        ReceiverLat = receiverLat;
        ReceiverLon = receiverLon;
        _path = cachePath;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<PolarHeatmap>.Instance;
    }

    public void Observe(double lat, double lon)
    {
        if (ReceiverLat is not double rLat || ReceiverLon is not double rLon)
        {
            return;
        }
        var dist = HaversineKm(rLat, rLon, lat, lon);
        if (dist <= 0)
        {
            return;
        }
        var bearing = BearingDeg(rLat, rLon, lat, lon);
        var bucket = (int)Math.Floor(bearing / BucketDeg) % Buckets;
        var band = Math.Min(Bands - 1, (int)(dist / BandKm));
        var day = (int)(_time.GetUtcNow().ToUnixTimeSeconds() / 86400);
        lock (_gate)
        {
            PruneLocked(day);
            if (!_dailyGrids.TryGetValue(day, out var grid))
            {
                grid = new int[Buckets, Bands];
                _dailyGrids[day] = grid;
            }
            grid[bucket, band]++;
            _dirty = true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _dailyGrids.Clear();
            _dirty = true;
        }
        _ = PersistAsync(force: true);
    }

    public PolarHeatmapSnapshot SnapshotView()
    {
        var today = (int)(_time.GetUtcNow().ToUnixTimeSeconds() / 86400);
        int[][] gridOut;
        int total = 0;
        lock (_gate)
        {
            PruneLocked(today);
            gridOut = new int[Buckets][];
            for (var b = 0; b < Buckets; b++)
            {
                gridOut[b] = new int[Bands];
            }
            foreach (var dayGrid in _dailyGrids.Values)
            {
                for (var b = 0; b < Buckets; b++)
                {
                    for (var d = 0; d < Bands; d++)
                    {
                        gridOut[b][d] += dayGrid[b, d];
                        total += dayGrid[b, d];
                    }
                }
            }
        }
        return new PolarHeatmapSnapshot(
            Receiver: new PolarHeatmapReceiver(ReceiverLat, ReceiverLon),
            BucketDeg: BucketDeg,
            BandKm: BandKm,
            Bands: Bands,
            WindowDays: WindowDays,
            Grid: gridOut,
            Total: total);
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
            var doc = JsonSerializer.Deserialize<PersistedPayload>(raw, JsonOpts);
            if (doc?.DailyGrids is null)
            {
                return;
            }
            var today = (int)(_time.GetUtcNow().ToUnixTimeSeconds() / 86400);
            var cutoff = today - (WindowDays - 1);
            var total = 0;
            lock (_gate)
            {
                _dailyGrids.Clear();
                foreach (var (key, val) in doc.DailyGrids)
                {
                    if (!int.TryParse(key, out var day) || day < cutoff || val is null)
                    {
                        continue;
                    }
                    var grid = CoerceGrid(val);
                    if (grid is null)
                    {
                        continue;
                    }
                    _dailyGrids[day] = grid;
                    for (var b = 0; b < Buckets; b++)
                    {
                        for (var d = 0; d < Bands; d++)
                        {
                            total += grid[b, d];
                        }
                    }
                }
            }
            _logger.LogInformation(
                "loaded polar heatmap ({Total} fixes over {Days} days in window)",
                total, _dailyGrids.Count);
        }
        catch (JsonException ex)
        {
            _logger.LogInformation(
                "polar heatmap cache at {Path} has an incompatible schema, starting fresh ({Reason})",
                _path, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "polar heatmap cache unreadable at {Path}", _path);
        }
    }

    public async Task MaybePersistAsync(TimeSpan interval, CancellationToken ct = default)
    {
        bool should;
        lock (_gate)
        {
            should = _dirty && (_time.GetUtcNow() - _lastPersist) >= interval;
        }
        if (should)
        {
            await PersistAsync(force: false, ct: ct);
        }
    }

    private async Task PersistAsync(bool force, CancellationToken ct = default)
    {
        if (_path is null)
        {
            return;
        }
        Dictionary<string, List<List<int>>> payload;
        lock (_gate)
        {
            if (!force && !_dirty)
            {
                return;
            }
            payload = new Dictionary<string, List<List<int>>>(_dailyGrids.Count);
            foreach (var (day, grid) in _dailyGrids)
            {
                var list = new List<List<int>>(Buckets);
                for (var b = 0; b < Buckets; b++)
                {
                    var row = new List<int>(Bands);
                    for (var d = 0; d < Bands; d++)
                    {
                        row.Add(grid[b, d]);
                    }
                    list.Add(row);
                }
                payload[day.ToString(System.Globalization.CultureInfo.InvariantCulture)] = list;
            }
        }
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(
                tmp,
                JsonSerializer.Serialize(new PersistedPayload { DailyGrids = payload }, JsonOpts),
                ct);
            File.Move(tmp, _path, overwrite: true);
            lock (_gate)
            {
                _dirty = false;
                _lastPersist = _time.GetUtcNow();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "couldn't persist polar heatmap");
        }
    }

    private void PruneLocked(int today)
    {
        var cutoff = today - (WindowDays - 1);
        var stale = _dailyGrids.Keys.Where(d => d < cutoff).ToList();
        foreach (var d in stale)
        {
            _dailyGrids.Remove(d);
        }
        if (stale.Count > 0)
        {
            _dirty = true;
        }
    }

    private static int[,]? CoerceGrid(List<List<int>> rows)
    {
        if (rows.Count != Buckets)
        {
            return null;
        }
        var grid = new int[Buckets, Bands];
        for (var b = 0; b < Buckets; b++)
        {
            var row = rows[b];
            if (row is null)
            {
                return null;
            }
            for (var d = 0; d < Bands && d < row.Count; d++)
            {
                grid[b, d] = row[d];
            }
        }
        return grid;
    }

    private static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = Deg2Rad(lat1);
        var phi2 = Deg2Rad(lat2);
        var dlon = Deg2Rad(lon2 - lon1);
        var y = Math.Sin(dlon) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dlon);
        var bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
        return (bearing + 360.0) % 360.0;
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = Deg2Rad(lat1);
        var phi2 = Deg2Rad(lat2);
        var dphi = Deg2Rad(lat2 - lat1);
        var dlam = Deg2Rad(lon2 - lon1);
        var a = Math.Pow(Math.Sin(dphi / 2), 2) + Math.Cos(phi1) * Math.Cos(phi2) * Math.Pow(Math.Sin(dlam / 2), 2);
        return 2 * PolarCoverage.EarthKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class PersistedPayload
    {
        public Dictionary<string, List<List<int>>>? DailyGrids { get; set; }
    }
}

public sealed record PolarHeatmapSnapshot(
    PolarHeatmapReceiver Receiver,
    double BucketDeg,
    double BandKm,
    int Bands,
    int WindowDays,
    IReadOnlyList<IReadOnlyList<int>> Grid,
    int Total);

public sealed record PolarHeatmapReceiver(double? Lat, double? Lon);
