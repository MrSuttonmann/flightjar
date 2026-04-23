using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Core.Stats;

/// <summary>
/// Per-bearing maximum-range tracker. For every accepted position we compute
/// the bearing + distance from the receiver and keep the max per-bucket.
/// Ports <c>app/coverage.py</c>.
/// </summary>
public sealed class PolarCoverage
{
    public const int Buckets = 36;
    public const double BucketDeg = 360.0 / Buckets;
    public const double EarthKm = 6371.0;

    private readonly object _gate = new();
    private readonly double[] _maxDist = new double[Buckets];
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private bool _dirty;
    private DateTimeOffset _lastPersist = DateTimeOffset.MinValue;

    public double? ReceiverLat { get; set; }
    public double? ReceiverLon { get; set; }

    /// <summary>Optional (bearing_deg, dist_km) callback fired when a bucket
    /// gets a new max.</summary>
    public Action<double, double>? OnNewMax { get; set; }

    public PolarCoverage(
        double? receiverLat = null, double? receiverLon = null,
        string? cachePath = null, TimeProvider? time = null,
        ILogger<PolarCoverage>? logger = null)
    {
        ReceiverLat = receiverLat;
        ReceiverLon = receiverLon;
        _path = cachePath;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<PolarCoverage>.Instance;
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
        bool emit = false;
        double angle = 0;
        lock (_gate)
        {
            if (dist > _maxDist[bucket])
            {
                _maxDist[bucket] = dist;
                _dirty = true;
                angle = bucket * BucketDeg + BucketDeg / 2;
                emit = true;
            }
        }
        if (emit && OnNewMax is not null)
        {
            try { OnNewMax(angle, dist); } catch { /* swallow */ }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_maxDist);
            _dirty = true;
        }
        _ = PersistAsync(force: true);
    }

    public PolarCoverageSnapshot SnapshotView()
    {
        var bearings = new List<BearingReading>();
        lock (_gate)
        {
            for (var i = 0; i < Buckets; i++)
            {
                if (_maxDist[i] > 0)
                {
                    bearings.Add(new BearingReading(
                        Angle: i * BucketDeg + BucketDeg / 2,
                        DistKm: Math.Round(_maxDist[i], 2)));
                }
            }
        }
        return new PolarCoverageSnapshot(
            Receiver: new PolarCoverageReceiver(ReceiverLat, ReceiverLon),
            BucketDeg: BucketDeg,
            Bearings: bearings);
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
            if (doc?.Maxdist is not null && doc.Maxdist.Count == Buckets)
            {
                lock (_gate)
                {
                    for (var i = 0; i < Buckets; i++)
                    {
                        _maxDist[i] = doc.Maxdist[i];
                    }
                }
                _logger.LogInformation("loaded polar coverage cache ({Buckets} buckets)", Buckets);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "polar coverage cache unreadable at {Path}", _path);
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
        double[] snapshot;
        lock (_gate)
        {
            if (!force && !_dirty)
            {
                return;
            }
            snapshot = (double[])_maxDist.Clone();
        }
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(
                tmp,
                JsonSerializer.Serialize(new PersistedPayload { Maxdist = snapshot.ToList() }, JsonOpts),
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
            _logger.LogWarning(ex, "couldn't persist polar coverage");
        }
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
        return 2 * EarthKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class PersistedPayload
    {
        public List<double>? Maxdist { get; set; }
    }
}

public sealed record PolarCoverageSnapshot(
    PolarCoverageReceiver Receiver,
    double BucketDeg,
    IReadOnlyList<BearingReading> Bearings);

public sealed record PolarCoverageReceiver(double? Lat, double? Lon);

public sealed record BearingReading(double Angle, double DistKm);
