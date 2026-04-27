using FlightJar.Core.Configuration;
using FlightJar.Core.Stats;
using FlightJar.Terrain.Srtm;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Computes and caches <see cref="BlackspotsGrid"/>s on demand, keyed on
/// target altitude. Behaviour:
///   1. If disabled (master flag off or LAT_REF/LON_REF missing), stay idle.
///   2. Otherwise: load nothing on startup. The first
///      <see cref="GetOrComputeAsync"/> call lazily reads the persisted
///      grid from disk (if present + config-compatible) and / or runs a
///      fresh compute. SRTM tiles (~26 MB each, ~12–15 for the default
///      radius) are loaded into memory only when a compute actually
///      needs them.
///   3. Background sweep: if no <c>/api/blackspots</c> request has hit
///      <see cref="GetOrComputeAsync"/> within the configured idle timeout
///      (<see cref="AppOptions.BlackspotsIdleTimeoutMinutes"/>, default
///      15 min), evict the LRU grid cache, the SRTM tile cache, and the
///      sampled ground elevation. Disk caches survive the eviction so
///      re-engaging the layer just pays a disk read.
/// <para>
/// <see cref="GetOrComputeAsync"/> is the hot path: returns the cached grid
/// for the requested altitude, or runs one synchronous LOS solve on a
/// background thread and caches the result. The shared tile set + ground
/// elevation are loaded once per "active session" and reused across
/// altitudes until idle eviction reclaims them.
/// </para>
/// <para>
/// Only valid grids persist to disk — the LRU memory cache covers
/// short-term hits. This keeps the persisted schema small and survives
/// antenna/radius/grid config changes cleanly (mismatched persisted
/// grids are simply skipped on next load).
/// </para>
/// </summary>
public sealed class BlackspotsWorker : BackgroundService
{
    private const int MaxCachedAltitudes = 8;

    /// <summary>How often the idle-eviction sweep checks the timer.
    /// Cheap (single comparison + occasional dictionary clear), so a
    /// short interval is fine.</summary>
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromMinutes(1);

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

    // Parallel cache for the high-resolution "blocking face" raster, keyed
    // on altitude same as the grid cache. Lives in memory only — the bytes
    // are too big to persist (a few hundred KB per altitude after PNG
    // compression) and the compute is fast enough to redo on demand.
    private readonly Dictionary<int, BlockerFaceRaster> _faceCache = new();

    // Once-loaded per-active-session tile set + ground elevation. Cleared
    // by idle eviction so re-engaging the feature reloads from disk fresh
    // (avoids stale state if the on-disk SRTM tiles changed underneath us).
    private double? _groundElevM;
    private int _tileCount;
    private readonly SemaphoreSlim _preloadGate = new(1, 1);

    // Last time something exercised the feature — used to decide when to
    // evict the in-memory caches. Updated by GetOrComputeAsync and
    // TriggerRecompute; read by the idle-sweep loop.
    private long _lastAccessTicks;

    // Has the persisted-grid file been read into the LRU cache yet?
    // First GetOrComputeAsync flips this so the load happens lazily
    // rather than on startup.
    private int _persistedLoaded;

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

        // No upfront load. Persisted grid + SRTM tiles are read on first
        // /api/blackspots access (see GetOrComputeAsync). The worker just
        // runs the idle-eviction sweep so memory gets reclaimed when the
        // feature isn't being used.
        _logger.LogInformation(
            "blackspots: ready (lazy load on first request, idle eviction after {Idle:F0} min)",
            _options.BlackspotsIdleTimeoutMinutes);

        if (_options.BlackspotsIdleTimeoutMinutes <= 0)
        {
            // Eviction disabled — sleep until cancellation.
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _time, stoppingToken);
            }
            catch (OperationCanceledException) { }
            return;
        }

        var idleTimeout = TimeSpan.FromMinutes(_options.BlackspotsIdleTimeoutMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IdleSweepInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException) { return; }

            try
            {
                EvictIfIdle(idleTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "blackspots: idle eviction sweep failed");
            }
        }
    }

    /// <summary>
    /// If the feature has been idle for at least <paramref name="idleTimeout"/>
    /// (no <see cref="GetOrComputeAsync"/> hits since the last access) and
    /// nothing is currently in the middle of a compute, drop the SRTM tile
    /// cache, the LRU grid cache, and the sampled ground elevation. Returns
    /// true when an eviction actually happened.
    /// </summary>
    public bool EvictIfIdle(TimeSpan idleTimeout)
    {
        if (idleTimeout <= TimeSpan.Zero)
        {
            return false;
        }
        var lastTicks = Interlocked.Read(ref _lastAccessTicks);
        if (lastTicks == 0)
        {
            // Never been hit — nothing in memory to evict.
            return false;
        }
        var lastAccess = new DateTimeOffset(lastTicks, TimeSpan.Zero);
        if (_time.GetUtcNow() - lastAccess < idleTimeout)
        {
            return false;
        }
        // Don't yank tiles out from under a running compute — wait for
        // it to finish on the next sweep tick.
        if (!_computeGate.Wait(0))
        {
            return false;
        }
        try
        {
            var (loaded, _) = _tiles.TileLoadSummary();
            if (loaded == 0 && _groundElevM is null)
            {
                // Already evicted (or never loaded). Reset the timer so we
                // don't re-log on every sweep when the feature stays idle.
                Interlocked.Exchange(ref _lastAccessTicks, 0);
                return false;
            }
            _logger.LogInformation(
                "blackspots: idle for {Idle:F1} min — evicting {Tiles} SRTM tile(s) and grid cache",
                (_time.GetUtcNow() - lastAccess).TotalMinutes, loaded);
            _tiles.EvictAll();
            InvalidateCache();
            _groundElevM = null;
            _tileCount = 0;
            // Force the persisted-grid lazy load to re-run on the next
            // request (in case the operator dropped a fresh file in /data
            // while the feature was idle).
            Interlocked.Exchange(ref _persistedLoaded, 0);
            Interlocked.Exchange(ref _lastAccessTicks, 0);
            return true;
        }
        finally
        {
            _computeGate.Release();
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

    /// <summary>
    /// Return the high-resolution "blocking face" raster for the given
    /// altitude — the per-pixel companion to the coarse cell grid that
    /// <see cref="GetOrComputeAsync"/> serves. Reuses the same SRTM tile
    /// preload + ground-elevation sample, so once tiles are warm the
    /// face compute pays only the per-pixel viewshed (~1-3 s).
    /// </summary>
    public async Task<BlockerFaceRaster?> GetOrComputeFaceAsync(double targetAltM, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }
        Interlocked.Exchange(ref _lastAccessTicks, _time.GetUtcNow().UtcTicks);

        var key = AltitudeKey(targetAltM);
        lock (_cacheGate)
        {
            if (_faceCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }
        await _computeGate.WaitAsync(ct);
        try
        {
            lock (_cacheGate)
            {
                if (_faceCache.TryGetValue(key, out var filled))
                {
                    return filled;
                }
            }
            await EnsurePreloadedAsync(ct);
            if (_groundElevM is not double groundElev)
            {
                return null;
            }
            var (loaded, empty) = _tiles.TileLoadSummary();
            var tilesWithData = loaded - empty;
            if (_tileCount > 1 && tilesWithData <= 1)
            {
                return null;
            }
            var faceParams = new BlockerFaceParams(
                ReceiverLat: _options.LatRef!.Value,
                ReceiverLon: _options.LonRef!.Value,
                AntennaMslM: ResolveAntennaMslM(groundElev),
                TargetAltitudeM: targetAltM,
                RadiusKm: _options.BlackspotsRadiusKm,
                GridDeg: _options.BlackspotsFaceGridDeg);
            _logger.LogInformation(
                "blackspots: computing face raster — antenna {Ant:F0} m MSL, target {Alt:F0} m MSL",
                faceParams.AntennaMslM, faceParams.TargetAltitudeM);
            var raster = await Task.Run(
                () => BlockerFaceCompute.Compute(faceParams, _tiles), ct);
            lock (_cacheGate)
            {
                _faceCache[key] = raster;
            }
            return raster;
        }
        finally
        {
            _computeGate.Release();
        }
    }

    /// <summary>Returns the grid for the given altitude, computing it on the fly if not cached.</summary>
    public async Task<BlackspotsGrid?> GetOrComputeAsync(double targetAltM, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }
        // Stamp access on the way in so the idle sweep treats this
        // request as activity even if it ends up being served from a
        // cached grid (no compute path → no other place to update).
        Interlocked.Exchange(ref _lastAccessTicks, _time.GetUtcNow().UtcTicks);

        // First-touch lazy load of the persisted grid. Interlocked guards
        // against a race between two simultaneous first-requests; the
        // loser sees _persistedLoaded == 1 and skips. Reset by EvictIfIdle.
        if (Interlocked.CompareExchange(ref _persistedLoaded, 1, 0) == 0)
        {
            try
            {
                await LoadPersistedAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "blackspots: persisted grid load failed");
            }
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
        Interlocked.Exchange(ref _lastAccessTicks, _time.GetUtcNow().UtcTicks);
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

    /// <summary>
    /// Re-hydrate the LRU grid cache from <c>blackspots.json.gz</c>. Cheap
    /// (one file read, no tile loads, no compute), so it's safe to run
    /// inline on the first request. Mismatched-config + invalid grids are
    /// dropped silently; persisted grids that survive the filter just
    /// shortcut the next compute for that altitude.
    /// </summary>
    private async Task LoadPersistedAsync(CancellationToken ct)
    {
        if (_persistPath is null) return;

        var wantParams = BuildParams(DefaultTargetAltitudeM, groundElevM: 0);
        var loaded = await BlackspotsGrid.LoadAllAsync(_persistPath, _logger, ct);
        if (loaded.Count == 0)
        {
            return;
        }
        var kept = 0;
        foreach (var grid in loaded)
        {
            if (!ParamsMatchIgnoringGround(grid.Params, wantParams)) continue;
            if (!grid.IsValid) continue;
            // _groundElevM / _tileCount stay null on purpose — they're
            // the "tiles are in memory" sentinel for EnsurePreloadedAsync.
            // Seeding them here without loading tiles would make every
            // non-cached compute see TileLoadSummary() == (0,0) and hit
            // the degenerate-tiles guard.
            Insert(AltitudeKey(grid.Params.TargetAltitudeM), grid);
            kept++;
        }
        _logger.LogInformation(
            "blackspots: hydrated {Kept}/{Total} persisted grid(s) on first request",
            kept, loaded.Count);
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
            _faceCache.Clear();
        }
    }
}
