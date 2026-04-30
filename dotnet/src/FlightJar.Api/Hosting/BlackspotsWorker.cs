using FlightJar.Api.Hosting.Blackspots;
using FlightJar.Core.Configuration;
using FlightJar.Core.Stats;
using FlightJar.Terrain.Srtm;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Orchestrates terrain-shadow grids on demand. Behaviour:
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
/// Composition: persistence + LRU + face cache live in <see cref="BlackspotsCache"/>;
/// SRTM preload + LOS solve + per-altitude progress live in
/// <see cref="BlackspotsCompute"/>. This worker holds the BackgroundService
/// shell, the access-time stamp that drives idle eviction, and the
/// composition glue.
/// </para>
/// </summary>
public sealed class BlackspotsWorker : BackgroundService
{
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

    private readonly BlackspotsCache _cache;
    private readonly BlackspotsCompute _compute;

    // Last time something exercised the feature — used to decide when to
    // evict the in-memory caches. Updated by GetOrComputeAsync and
    // TriggerRecompute; read by the idle-sweep loop.
    private long _lastAccessTicks;

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
        _cache = new BlackspotsCache(persistPath, logger);
        _compute = new BlackspotsCompute(options, tiles, time, logger);
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
        if (!_compute.TryWaitExclusive())
        {
            return false;
        }
        try
        {
            var (loaded, _) = _tiles.TileLoadSummary();
            if (loaded == 0 && !_compute.IsSessionLoaded)
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
            _cache.Clear();
            _compute.ResetSession();
            // Force the persisted-grid lazy load to re-run on the next
            // request (in case the operator dropped a fresh file in /data
            // while the feature was idle).
            _cache.ResetPersistedLoaded();
            Interlocked.Exchange(ref _lastAccessTicks, 0);
            return true;
        }
        finally
        {
            _compute.ReleaseExclusive();
        }
    }

    /// <summary>
    /// Live progress snapshot for the compute currently running at
    /// <paramref name="targetAltM"/>. Returns
    /// <c>(false, 0, Idle)</c> when that altitude is cached, queued behind a
    /// different compute, or the feature is idle. Phase + fraction together
    /// describe which part of the pipeline (preload, cell-grid solve, face
    /// raster) is active.
    /// </summary>
    public BlackspotsProgressSnapshot GetProgress(double targetAltM) =>
        _compute.GetProgress(targetAltM);

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
        if (_cache.TryGetFace(key) is BlockerFaceRaster cached)
        {
            return cached;
        }
        await _compute.WaitExclusiveAsync(ct);
        try
        {
            if (_cache.TryGetFace(key) is BlockerFaceRaster filled)
            {
                return filled;
            }
            var raster = await _compute.ComputeFaceAsync(targetAltM, ct);
            if (raster is not null)
            {
                _cache.InsertFace(key, raster);
            }
            return raster;
        }
        finally
        {
            _compute.ReleaseExclusive();
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

        // First-touch lazy load of the persisted grid. Cache's claim
        // returns true exactly once per eviction cycle.
        if (_cache.TryClaimPersistedLoad())
        {
            try
            {
                await _cache.LoadPersistedAsync(_compute.MatchesLiveConfig, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "blackspots: persisted grid load failed");
            }
        }

        var key = AltitudeKey(targetAltM);
        if (_cache.TryGet(key) is BlackspotsGrid cached)
        {
            return cached;
        }
        await _compute.WaitExclusiveAsync(ct);
        try
        {
            // Another caller may have filled the slot while we were waiting.
            if (_cache.TryGet(key) is BlackspotsGrid filled)
            {
                return filled;
            }
            var grid = await _compute.ComputeAtAsync(targetAltM, ct);
            if (grid is not null)
            {
                _cache.Insert(key, grid);
                await _cache.PersistAsync(ct);
            }
            return grid;
        }
        finally
        {
            _compute.ReleaseExclusive();
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
                await _compute.WaitExclusiveAsync(CancellationToken.None);
                try
                {
                    _cache.Clear();
                    _compute.ResetSession();
                    var grid = await _compute.ComputeAtAsync(DefaultTargetAltitudeM, CancellationToken.None);
                    if (grid is not null)
                    {
                        _cache.Insert(AltitudeKey(DefaultTargetAltitudeM), grid);
                        await _cache.PersistAsync(CancellationToken.None);
                    }
                }
                finally
                {
                    _compute.ReleaseExclusive();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "blackspots manual recompute failed");
            }
        });
    }

    /// <summary>Per-altitude grid radius. Floors at the operator-configured
    /// <see cref="AppOptions.BlackspotsRadiusKm"/> and grows up to the radio
    /// horizon for this antenna / target pair, capped at the env validator's
    /// 1000 km ceiling. See <see cref="BlackspotsCompute.EffectiveRadiusKm"/>
    /// for the full derivation.</summary>
    internal double EffectiveRadiusKm(double antennaMslM, double targetAltM, double groundElevM) =>
        _compute.EffectiveRadiusKm(antennaMslM, targetAltM, groundElevM);

    private static int AltitudeKey(double altM) => (int)Math.Round(altM);
}
