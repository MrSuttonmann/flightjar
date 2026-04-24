using FlightJar.Core.Configuration;
using FlightJar.Core.Stats;
using FlightJar.Terrain.Srtm;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Computes and caches <see cref="BlackspotsGrid"/>s on demand, keyed on
/// target altitude. Startup:
///   1. If disabled (master flag off or LAT_REF/LON_REF missing), stay idle.
///   2. Otherwise load any previously-persisted default-altitude grid. If its
///      non-altitude params still match the live config, seed the cache with
///      it. Else kick off a fresh compute for the configured default.
/// <para>
/// <see cref="GetOrComputeAsync"/> is the hot path: returns the cached grid
/// for the requested altitude, or runs one synchronous LOS solve on a
/// background thread and caches the result. The shared tile set + ground
/// elevation are preloaded once; altitude changes reuse them.
/// </para>
/// <para>
/// Only the default altitude's grid persists to disk — the LRU memory cache
/// covers the rest. This keeps the persisted schema small and survives
/// antenna/radius/grid config changes cleanly (the full cache is invalidated
/// and the default altitude recomputed).
/// </para>
/// </summary>
public sealed class BlackspotsWorker : BackgroundService
{
    private const int MaxCachedAltitudes = 8;

    /// <summary>
    /// Default target altitude used to prewarm one grid at startup so the
    /// first frontend toggle-on doesn't have to wait on a cold compute.
    /// FL100 (3048 m MSL) is a reasonable GA / lower-airway cruise altitude
    /// — mid-range of the slider — chosen so the "likely first value a user
    /// picks" is the one that's already cached.
    /// </summary>
    public const double DefaultTargetAltitudeM = 3048.0;

    private readonly AppOptions _options;
    private readonly SrtmTileStore _tiles;
    private readonly TimeProvider _time;
    private readonly ILogger<BlackspotsWorker> _logger;
    private readonly string? _persistPath;
    private readonly SemaphoreSlim _computeGate = new(1, 1);

    // LRU cache: LinkedList tracks access order (head = most recent);
    // _nodes is the O(1) lookup into that list by altitude key.
    private readonly object _cacheGate = new();
    private readonly LinkedList<BlackspotsGrid> _cacheOrder = new();
    private readonly Dictionary<int, LinkedListNode<BlackspotsGrid>> _nodes = new();

    // Once-computed per-session tile set + ground elevation. Reused across
    // altitudes since none of the altitude-independent inputs ever change
    // mid-session.
    private double? _groundElevM;
    private int _tileCount;
    private readonly SemaphoreSlim _preloadGate = new(1, 1);

    // Live-progress readout for the one compute that's active right now.
    // _computeGate already serialises computes, so there's never more than
    // a single active altitude key here.
    private readonly object _progressGate = new();
    private int? _activeAltitudeKey;
    private double _activeProgress;

    public BlackspotsWorker(
        AppOptions options,
        SrtmTileStore tiles,
        TimeProvider time,
        ILogger<BlackspotsWorker> logger,
        string? persistPath)
    {
        _options = options;
        _tiles = tiles;
        _time = time;
        _logger = logger;
        _persistPath = persistPath;
    }

    /// <summary>
    /// True iff the feature is active — LAT_REF / LON_REF configured and the
    /// master enable flag is on.
    /// </summary>
    public bool Enabled =>
        _options.BlackspotsEnabled && _options.LatRef is double && _options.LonRef is double;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _logger.LogInformation(
                "blackspots disabled ({Reason})",
                _options.BlackspotsEnabled ? "LAT_REF / LON_REF not set" : "BLACKSPOTS_ENABLED=0");
            return;
        }

        try
        {
            await LoadOrComputeDefaultAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "blackspots initial load/compute failed");
        }
    }

    /// <summary>
    /// Live progress snapshot for the compute currently running at
    /// <paramref name="targetAltM"/>. Returns <c>(false, 0)</c> when that
    /// altitude is cached, queued behind a different compute, or the feature
    /// is idle.
    /// </summary>
    public (bool Active, double Progress) GetProgress(double targetAltM)
    {
        var key = AltitudeKey(targetAltM);
        lock (_progressGate)
        {
            return _activeAltitudeKey == key ? (true, _activeProgress) : (false, 0.0);
        }
    }

    /// <summary>Returns the grid for the given altitude, computing it on the fly if not cached.</summary>
    public async Task<BlackspotsGrid?> GetOrComputeAsync(double targetAltM, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }
        var key = AltitudeKey(targetAltM);
        if (TryGetCached(key) is BlackspotsGrid cached)
        {
            return cached;
        }
        await _computeGate.WaitAsync(ct);
        try
        {
            // Another caller may have filled the slot while we were waiting.
            if (TryGetCached(key) is BlackspotsGrid filled)
            {
                return filled;
            }
            var grid = await ComputeAtAsync(targetAltM, ct);
            if (grid is not null)
            {
                Insert(key, grid);
                await PersistCacheAsync(ct);
            }
            return grid;
        }
        finally
        {
            _computeGate.Release();
        }
    }

    /// <summary>
    /// Invalidate the memory cache and recompute the default altitude. Fire-
    /// and-forget; errors are logged but not propagated. Used by the
    /// <c>/api/blackspots/recompute</c> endpoint after config changes.
    /// </summary>
    public void TriggerRecompute()
    {
        if (!Enabled)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await _computeGate.WaitAsync(CancellationToken.None);
                try
                {
                    InvalidateCache();
                    _groundElevM = null;
                    var grid = await ComputeAtAsync(DefaultTargetAltitudeM, CancellationToken.None);
                    if (grid is not null)
                    {
                        Insert(AltitudeKey(DefaultTargetAltitudeM), grid);
                        await PersistCacheAsync(CancellationToken.None);
                    }
                }
                finally
                {
                    _computeGate.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "blackspots manual recompute failed");
            }
        });
    }

    private async Task LoadOrComputeDefaultAsync(CancellationToken ct)
    {
        await _computeGate.WaitAsync(ct);
        try
        {
            var wantParams = BuildParams(DefaultTargetAltitudeM, groundElevM: 0);
            var seeded = 0;

            if (_persistPath is not null)
            {
                var loaded = await BlackspotsGrid.LoadAllAsync(_persistPath, _logger, ct);
                var kept = 0;
                foreach (var grid in loaded)
                {
                    if (!ParamsMatchIgnoringGround(grid.Params, wantParams))
                    {
                        continue;
                    }
                    if (!grid.IsValid)
                    {
                        // Stale invalid grid from a prior degenerate run — drop it.
                        continue;
                    }
                    // Seed the LRU cache only. _groundElevM and _tileCount are
                    // deliberately NOT seeded from persisted data — they double
                    // as the "tiles are loaded in memory" sentinel for
                    // EnsurePreloadedAsync, and setting them here without
                    // actually loading tiles would make every subsequent
                    // altitude compute see TileLoadSummary() == (0,0) and hit
                    // the degenerate-tiles guard.
                    Insert(AltitudeKey(grid.Params.TargetAltitudeM), grid);
                    kept++;
                }
                if (loaded.Count > 0)
                {
                    _logger.LogInformation(
                        "blackspots: loaded {Kept}/{Total} persisted grid(s) matching current config",
                        kept, loaded.Count);
                }
                seeded = kept;
            }

            // Always ensure the default altitude is populated so the first
            // frontend toggle-on doesn't pay a cold compute.
            if (TryGetCached(AltitudeKey(DefaultTargetAltitudeM)) is null)
            {
                var grid = await ComputeAtAsync(DefaultTargetAltitudeM, ct);
                if (grid is not null)
                {
                    Insert(AltitudeKey(DefaultTargetAltitudeM), grid);
                    await PersistCacheAsync(ct);
                }
            }
            else if (seeded > 0)
            {
                _logger.LogInformation(
                    "blackspots: default altitude ({Alt:F0} m MSL) served from persisted cache",
                    DefaultTargetAltitudeM);
                // Default grid is already cached, so ComputeAtAsync never ran
                // and tiles aren't in memory yet. Warm them in the background
                // so the user's first non-default altitude pick doesn't block
                // on reading ~100 SRTM tiles from disk.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await EnsurePreloadedAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "blackspots: background tile preload failed");
                    }
                }, CancellationToken.None);
            }
        }
        finally
        {
            _computeGate.Release();
        }
    }

    /// <summary>
    /// Preload receiver tiles, sample ground elevation, run LOS solver for
    /// the given altitude. Expects to be called under <see cref="_computeGate"/>.
    /// Returns null if the compute is cancelled. Persistence is handled by
    /// the caller via <see cref="PersistCacheAsync"/> after inserting the
    /// returned grid into the cache.
    /// </summary>
    private async Task<BlackspotsGrid?> ComputeAtAsync(double targetAltM, CancellationToken ct)
    {
        await EnsurePreloadedAsync(ct);
        if (_groundElevM is not double groundElev)
        {
            return null;
        }
        var fullParams = BuildParams(targetAltM, groundElev);
        var antennaAgl = fullParams.AntennaMslM - fullParams.GroundElevationM;
        var (loaded, empty) = _tiles.TileLoadSummary();
        var tilesWithData = loaded - empty;

        // Skip the per-cell LOS solve when the tile preload came back
        // degenerate — a multi-tile bbox with ≤1 tile returning elevation
        // data almost always means SRTM downloads are blocked, and the
        // compute would spend 1-2 s producing an earth-curvature-only
        // "perfect circle" that's actively misleading. Emit an empty grid
        // with the same tile-coverage stats so the frontend surfaces the
        // warning banner; IsValid = false keeps it out of persistence.
        if (_tileCount > 1 && tilesWithData <= 1)
        {
            _logger.LogWarning(
                "blackspots: skipping compute at {Alt:F0} m MSL — only {DataTiles}/{Expected} SRTM tile(s) have elevation data (downloads likely blocked)",
                targetAltM, tilesWithData, _tileCount);
            return new BlackspotsGrid(
                fullParams, _time.GetUtcNow(), _tileCount, tilesWithData,
                Array.Empty<BlackspotCell>());
        }

        _logger.LogInformation(
            "blackspots: computing grid — receiver ({Lat:F4}, {Lon:F4}), ground {Elev:F0} m MSL, antenna {AntMsl:F0} m MSL ({AntAgl:F1} m AGL), target {Alt:F0} m MSL, radius {Radius:F0} km",
            fullParams.ReceiverLat, fullParams.ReceiverLon,
            fullParams.GroundElevationM, fullParams.AntennaMslM, antennaAgl,
            fullParams.TargetAltitudeM, fullParams.RadiusKm);

        var altKey = AltitudeKey(targetAltM);
        SetProgress(altKey, 0);
        try
        {
            return await Task.Run(() => BlackspotsGrid.Compute(
                fullParams, _tiles, _tileCount, tilesWithData, _time, _logger,
                onProgress: frac => SetProgress(altKey, frac),
                ct: ct), ct);
        }
        finally
        {
            ClearProgress(altKey);
        }
    }

    /// <summary>
    /// Write every valid entry in the LRU cache to disk. Called after each
    /// successful compute. Invalid grids (the "perfect circle" signal) are
    /// omitted so a single bad network session can't poison future restarts.
    /// </summary>
    private async Task PersistCacheAsync(CancellationToken ct)
    {
        if (_persistPath is null)
        {
            return;
        }
        List<BlackspotsGrid> toSave;
        lock (_cacheGate)
        {
            toSave = _cacheOrder.Where(g => g.IsValid).ToList();
        }
        if (toSave.Count == 0)
        {
            return;
        }
        await BlackspotsGrid.SaveAllAsync(_persistPath, toSave, _logger, ct);
    }

    private void SetProgress(int altKey, double fraction)
    {
        lock (_progressGate)
        {
            _activeAltitudeKey = altKey;
            _activeProgress = fraction;
        }
    }

    private void ClearProgress(int altKey)
    {
        lock (_progressGate)
        {
            if (_activeAltitudeKey == altKey)
            {
                _activeAltitudeKey = null;
                _activeProgress = 0.0;
            }
        }
    }

    /// <summary>
    /// Download / load the SRTM tiles covering the receiver's bbox and sample
    /// the receiver's ground elevation. Idempotent — after the first
    /// successful call <see cref="_groundElevM"/> is set and this is a no-op.
    /// </summary>
    private async Task EnsurePreloadedAsync(CancellationToken ct)
    {
        if (_groundElevM is not null)
        {
            return;
        }
        await _preloadGate.WaitAsync(ct);
        try
        {
            if (_groundElevM is not null)
            {
                return;
            }
            var (minLat, maxLat, minLon, maxLon) = BlackspotsGrid.BboxFor(
                _options.LatRef!.Value, _options.LonRef!.Value, _options.BlackspotsRadiusKm);
            var neededTiles = SrtmTileStore.TilesForBbox(minLat, maxLat, minLon, maxLon).ToList();
            _logger.LogInformation(
                "blackspots: loading {Tiles} SRTM tile(s) for bbox [{MinLat:F2}, {MinLon:F2}]–[{MaxLat:F2}, {MaxLon:F2}]",
                neededTiles.Count, minLat, minLon, maxLat, maxLon);
            await _tiles.EnsureLoadedAsync(neededTiles, ct: ct);
            _tileCount = neededTiles.Count;
            _groundElevM = _tiles.ElevationMetres(_options.LatRef!.Value, _options.LonRef!.Value);

            // Sanity-check the load. An "everything is flat sea level" result
            // (= perfect circle blackspot output) is almost always a symptom
            // of tile downloads being blocked by a firewall / proxy rather
            // than the user's receiver actually being at sea level on flat
            // terrain. Log loudly.
            var (loaded, empty) = _tiles.TileLoadSummary();
            var dataTiles = loaded - empty;
            _logger.LogInformation(
                "blackspots: SRTM tiles loaded {Loaded}/{Expected} ({DataTiles} with data, {Empty} empty/ocean)",
                loaded, neededTiles.Count, dataTiles, empty);
            if (neededTiles.Count > 1 && dataTiles <= 1)
            {
                _logger.LogWarning(
                    "blackspots: only {DataTiles} SRTM tile(s) returned elevation data out of {Expected} expected — "
                    + "downloads may be blocked by a firewall or proxy. The blackspot grid will look like a perfect "
                    + "circle (earth-curvature horizon only) until tile access is restored.",
                    dataTiles, neededTiles.Count);
            }
        }
        finally
        {
            _preloadGate.Release();
        }
    }

    /// <summary>
    /// Resolve the antenna tip MSL from the configured options. If
    /// <see cref="AppOptions.BlackspotsAntennaMslM"/> is set, use it directly
    /// (user-measured values are trusted over the DEM's guess). Otherwise
    /// derive from the AGL fallback + the DEM-sampled ground elevation.
    /// </summary>
    private double ResolveAntennaMslM(double groundElevM) =>
        _options.BlackspotsAntennaMslM ?? (groundElevM + _options.BlackspotsAntennaAglM);

    private BlackspotsParams BuildParams(double targetAltM, double groundElevM) =>
        new(
            ReceiverLat: _options.LatRef!.Value,
            ReceiverLon: _options.LonRef!.Value,
            GroundElevationM: groundElevM,
            AntennaMslM: ResolveAntennaMslM(groundElevM),
            TargetAltitudeM: targetAltM,
            RadiusKm: _options.BlackspotsRadiusKm,
            GridDeg: _options.BlackspotsGridDeg,
            MaxAglM: _options.BlackspotsMaxAglM);

    /// <summary>
    /// Ground elevation + target altitude are allowed to differ — everything
    /// else (receiver coords, antenna height, radius, grid, max AGL) must
    /// match exactly. Used on startup to decide whether a persisted default-
    /// altitude grid is still usable.
    /// </summary>
    private static bool ParamsMatchIgnoringGround(BlackspotsParams persisted, BlackspotsParams live) =>
        persisted with { GroundElevationM = 0, TargetAltitudeM = 0 }
            == live with { GroundElevationM = 0, TargetAltitudeM = 0 };

    private static int AltitudeKey(double altM) => (int)Math.Round(altM);

    private BlackspotsGrid? TryGetCached(int key)
    {
        lock (_cacheGate)
        {
            if (_nodes.TryGetValue(key, out var node))
            {
                _cacheOrder.Remove(node);
                _cacheOrder.AddFirst(node);
                return node.Value;
            }
            return null;
        }
    }

    private void Insert(int key, BlackspotsGrid grid)
    {
        lock (_cacheGate)
        {
            if (_nodes.TryGetValue(key, out var existing))
            {
                _cacheOrder.Remove(existing);
                _nodes.Remove(key);
            }
            var node = _cacheOrder.AddFirst(grid);
            _nodes[key] = node;
            while (_cacheOrder.Count > MaxCachedAltitudes)
            {
                var last = _cacheOrder.Last!;
                _cacheOrder.RemoveLast();
                _nodes.Remove(AltitudeKey(last.Value.Params.TargetAltitudeM));
            }
        }
    }

    private void InvalidateCache()
    {
        lock (_cacheGate)
        {
            _cacheOrder.Clear();
            _nodes.Clear();
        }
    }
}
