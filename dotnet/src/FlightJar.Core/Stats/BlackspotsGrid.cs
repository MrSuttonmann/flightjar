using System.IO.Compression;
using System.Text.Json;
using FlightJar.Terrain;
using FlightJar.Terrain.LineOfSight;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Core.Stats;

/// <summary>
/// The receiver/environment knobs a blackspots computation is parameterised by.
/// Two grids are considered interchangeable iff every field matches exactly,
/// which is how the worker decides whether to reuse a disk-cached result or
/// recompute from scratch.
/// </summary>
/// <param name="GroundElevationM">
/// DEM-sampled ground elevation at the receiver — informational only
/// (used for the <c>MaxAglM</c> ceiling and so the frontend can surface
/// "antenna is X m above local terrain" context). Absolute antenna altitude
/// is carried by <paramref name="AntennaMslM"/>.
/// </param>
public sealed record BlackspotsParams(
    double ReceiverLat,
    double ReceiverLon,
    double GroundElevationM,
    double AntennaMslM,
    double TargetAltitudeM,
    double RadiusKm,
    double GridDeg,
    double MaxAglM);

/// <summary>
/// A single blocked grid cell. <see cref="RequiredAntennaMslM"/> is the
/// minimum antenna-tip altitude in metres MSL that would restore LOS to the
/// target altitude at this cell. Null means "unreachable": LOS was still
/// blocked at <c>GroundElevationM + MaxAglM</c>.
/// </summary>
public sealed record BlackspotCell(double Lat, double Lon, double? RequiredAntennaMslM);

/// <summary>
/// Serialisable wire / on-disk payload. Unchanged by the worker — computed
/// once, handed to HTTP readers via <see cref="BlackspotsGrid.SnapshotView"/>.
/// </summary>
/// <param name="TilesWithData">
/// How many of the loaded SRTM tiles actually had elevation data (as opposed
/// to being empty/ocean sentinels). When this drops to 0-1 over a multi-tile
/// bbox the user is seeing a "perfect circle" of earth-bulge-only blockages
/// rather than real terrain shadows — the frontend surfaces a warning.
/// </param>
public sealed record BlackspotsSnapshot(
    bool Enabled,
    BlackspotsParams? Params,
    DateTimeOffset? ComputedAt,
    int TileCount,
    int TilesWithData,
    IReadOnlyList<BlackspotCell> Cells);

/// <summary>
/// Pre-computed receiver "blackspots" — grid cells where the radio LOS to a
/// given target altitude is terrain-blocked. Mirrors <see cref="PolarCoverage"/>'s
/// shape: snapshot view, atomic gzipped-JSON persistence, load-or-compute on
/// startup. The compute is a cold one-off at startup (or when
/// <see cref="BlackspotsParams"/> change), so <see cref="ComputeAsync"/> is
/// async and does all its own I/O.
/// </summary>
public sealed class BlackspotsGrid
{
    public const int SchemaVersion = 3;

    /// <summary>
    /// True if the grid looks credible — either the bbox only needed one SRTM
    /// tile (single-tile computes are trivially fine) or more than one of the
    /// loaded tiles actually returned data. A <c>false</c> here is the "perfect
    /// circle" signal: downloads were blocked and the only "blockages" are
    /// earth-curvature artefacts. Used by the worker to decide whether to
    /// persist a compute to disk.
    /// </summary>
    public bool IsValid => TileCount <= 1 || TilesWithData > 1;

    public BlackspotsParams Params { get; }
    public DateTimeOffset ComputedAt { get; }
    public int TileCount { get; }
    public int TilesWithData { get; }
    public IReadOnlyList<BlackspotCell> Cells { get; }

    public BlackspotsGrid(
        BlackspotsParams @params,
        DateTimeOffset computedAt,
        int tileCount,
        int tilesWithData,
        IReadOnlyList<BlackspotCell> cells)
    {
        Params = @params;
        ComputedAt = computedAt;
        TileCount = tileCount;
        TilesWithData = tilesWithData;
        Cells = cells;
    }

    public BlackspotsSnapshot SnapshotView() =>
        new(Enabled: true, Params: Params, ComputedAt: ComputedAt,
            TileCount: TileCount, TilesWithData: TilesWithData, Cells: Cells);

    /// <summary>Axis-aligned lat/lon bbox that bounds the computation radius.</summary>
    public static (double MinLat, double MaxLat, double MinLon, double MaxLon) BboxFor(
        double receiverLat, double receiverLon, double radiusKm)
    {
        const double kmPerDegLat = 111.32;
        var latRad = receiverLat * Math.PI / 180.0;
        var dlat = radiusKm / kmPerDegLat;
        var dlon = radiusKm / (kmPerDegLat * Math.Max(0.01, Math.Cos(latRad)));
        return (receiverLat - dlat, receiverLat + dlat, receiverLon - dlon, receiverLon + dlon);
    }

    /// <summary>
    /// Compute the grid from a pre-loaded terrain sampler. Enumerates cells
    /// inside the configured radius, runs the LOS solver per cell, and collects
    /// blocked ones with their required-antenna-height solution. Cells are
    /// solved in parallel — the solver and <see cref="ITerrainSampler"/>
    /// implementations are pure / safe for concurrent reads — so wall-time
    /// scales near-linearly with <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    /// <param name="onProgress">
    /// Optional progress callback invoked as cells are solved. Receives a
    /// fraction in [0, 1]. Called at most every 2 % of progress so a caller
    /// polling it at ~3 Hz never misses significant movement.
    /// </param>
    public static BlackspotsGrid Compute(
        BlackspotsParams @params,
        ITerrainSampler sampler,
        int tileCount = 0,
        int tilesWithData = 0,
        TimeProvider? time = null,
        ILogger? logger = null,
        Action<double>? onProgress = null,
        CancellationToken ct = default)
    {
        time ??= TimeProvider.System;
        logger ??= NullLogger.Instance;

        var (minLat, maxLat, minLon, maxLon) = BboxFor(
            @params.ReceiverLat, @params.ReceiverLon, @params.RadiusKm);

        var receiver = new LosReceiver(
            @params.ReceiverLat, @params.ReceiverLon, @params.AntennaMslM);
        // Bisection ceiling — how high we'd consider raising the antenna,
        // expressed as absolute MSL. "MaxAglM metres above local ground"
        // reads naturally to users; we translate it here so the solver is
        // pure MSL internally. Ensures the ceiling is at least as high as
        // the current antenna (guards against user setting AntennaMslM
        // higher than ground + MaxAglM).
        var ceilingMslM = Math.Max(
            @params.AntennaMslM, @params.GroundElevationM + @params.MaxAglM);

        // Enumerate the in-radius cells once up-front. Pre-filtering (rather
        // than doing the distance check per-worker) gives every cell in the
        // parallel loop comparable work — otherwise the top/bottom rows of
        // the bbox, which are mostly outside the inscribed radius, bail out
        // in microseconds and let their chunks' workers spike `stepCount`
        // while middle-row workers are still grinding on real LOS solves.
        // The result is progress that climbs linearly in wall time instead
        // of jumping from small to big values.
        var coords = new List<(double Lat, double Lon)>();
        var radiusMetres = @params.RadiusKm * 1000.0;
        for (var lat = minLat; lat <= maxLat; lat += @params.GridDeg)
        {
            for (var lon = minLon; lon <= maxLon; lon += @params.GridDeg)
            {
                var d = GreatCircle.DistanceMetres(@params.ReceiverLat, @params.ReceiverLon, lat, lon);
                if (d <= radiusMetres && d >= 1.0)
                {
                    coords.Add((lat, lon));
                }
            }
        }
        var totalCells = coords.Count;

        var cells = new List<BlackspotCell>();
        var cellsGate = new object();
        var stepCount = 0;
        var progressGate = new object();
        var lastReported = 0.0;
        var options = new ParallelOptions { CancellationToken = ct };

        Parallel.For(0, totalCells, options,
            localInit: () => new List<BlackspotCell>(),
            body: (i, _, local) =>
            {
                var (lat, lon) = coords[i];
                // TargetAltitudeM == 0 means "plane on the ground" —
                // use the DEM elevation at this cell plus a small fuselage
                // offset so we're not targeting the dirt itself. Anything
                // positive is treated as an absolute MSL altitude.
                var targetMslM = @params.TargetAltitudeM > 0
                    ? @params.TargetAltitudeM
                    : sampler.ElevationMetres(lat, lon) + 2.0;
                var target = new LosTarget(lat, lon, targetMslM);
                var result = LineOfSightSolver.Solve(
                    receiver, target, sampler, ceilingMslM: ceilingMslM);
                if (result.Blocked)
                {
                    local.Add(new BlackspotCell(
                        Math.Round(lat, 4), Math.Round(lon, 4), result.RequiredAntennaMslM));
                }
                if (onProgress is not null && totalCells > 0)
                {
                    var done = Interlocked.Increment(ref stepCount);
                    var frac = (double)done / totalCells;
                    // Callback runs under the lock so reports can never arrive
                    // out of order — without this, a thread with frac=0.10
                    // that was descheduled just after releasing the gate can
                    // clobber a faster thread's already-delivered frac=0.12.
                    // The fire rate is capped at ~50 calls per compute so the
                    // extra hold time is negligible.
                    lock (progressGate)
                    {
                        if (frac - lastReported >= 0.02 || done == totalCells)
                        {
                            lastReported = frac;
                            try { onProgress(frac); } catch { /* never fail a compute on a progress hiccup */ }
                        }
                    }
                }
                return local;
            },
            localFinally: local =>
            {
                if (local.Count == 0)
                {
                    return;
                }
                lock (cellsGate)
                {
                    cells.AddRange(local);
                }
            });

        logger.LogInformation(
            "blackspots: solved {Steps} cells, {Blocked} blocked ({Pct:F1}%) across {Workers} worker(s)",
            totalCells, cells.Count, totalCells == 0 ? 0 : cells.Count * 100.0 / totalCells,
            Environment.ProcessorCount);

        return new BlackspotsGrid(@params, time.GetUtcNow(), tileCount, tilesWithData, cells);
    }

    /// <summary>
    /// Load every previously-persisted grid from <paramref name="path"/>.
    /// Returns an empty list on missing / corrupt / schema-mismatched files
    /// — the caller treats that as "start with an empty cache". The file
    /// carries a list keyed by target altitude; the worker picks out the
    /// entries whose non-altitude params still match the live config.
    /// </summary>
    public static async Task<IReadOnlyList<BlackspotsGrid>> LoadAllAsync(
        string path, ILogger? logger = null, CancellationToken ct = default)
    {
        logger ??= NullLogger.Instance;
        if (!File.Exists(path))
        {
            return Array.Empty<BlackspotsGrid>();
        }
        try
        {
            await using var file = File.OpenRead(path);
            await using var gz = new GZipStream(file, CompressionMode.Decompress);
            var payload = await JsonSerializer.DeserializeAsync<PersistedPayload>(gz, JsonOpts, ct);
            if (payload is null || payload.Version != SchemaVersion)
            {
                return Array.Empty<BlackspotsGrid>();
            }
            var grids = new List<BlackspotsGrid>();
            foreach (var entry in payload.Entries ?? new List<PersistedEntry>())
            {
                if (entry.Params is null)
                {
                    continue;
                }
                grids.Add(new BlackspotsGrid(
                    entry.Params, entry.ComputedAt, entry.TileCount, entry.TilesWithData,
                    entry.Cells ?? new List<BlackspotCell>()));
            }
            return grids;
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                "blackspots cache at {Path} unreadable — starting fresh ({Reason})",
                path, ex.Message);
            return Array.Empty<BlackspotsGrid>();
        }
    }

    /// <summary>
    /// Atomically persist a set of grids — typically the full in-memory cache
    /// — to <paramref name="path"/>: gzipped JSON, temp file + rename.
    /// Swallows errors after logging them; persistence failures must never
    /// take the caller down.
    /// </summary>
    public static async Task SaveAllAsync(
        string path, IEnumerable<BlackspotsGrid> grids,
        ILogger? logger = null, CancellationToken ct = default)
    {
        logger ??= NullLogger.Instance;
        string? tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            await using (var file = File.Create(tmp))
            await using (var gz = new GZipStream(file, CompressionLevel.Optimal))
            {
                var payload = new PersistedPayload
                {
                    Version = SchemaVersion,
                    Entries = grids.Select(g => new PersistedEntry
                    {
                        Params = g.Params,
                        ComputedAt = g.ComputedAt,
                        TileCount = g.TileCount,
                        TilesWithData = g.TilesWithData,
                        Cells = g.Cells.ToList(),
                    }).ToList(),
                };
                await JsonSerializer.SerializeAsync(gz, payload, JsonOpts, ct);
            }
            File.Move(tmp, path, overwrite: true);
            tmp = null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "couldn't persist blackspots grids at {Path}", path);
        }
        finally
        {
            if (tmp is not null && File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* best-effort */ }
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class PersistedPayload
    {
        public int Version { get; set; }
        public List<PersistedEntry>? Entries { get; set; }
    }

    private sealed class PersistedEntry
    {
        public BlackspotsParams? Params { get; set; }
        public DateTimeOffset ComputedAt { get; set; }
        public int TileCount { get; set; }
        public int TilesWithData { get; set; }
        public List<BlackspotCell>? Cells { get; set; }
    }
}
