using FlightJar.Core.Configuration;
using FlightJar.Core.Stats;
using FlightJar.Terrain.LineOfSight;
using FlightJar.Terrain.Srtm;

namespace FlightJar.Api.Hosting.Blackspots;

/// <summary>
/// Owns the SRTM-tile preload + LOS solve hot path. Wraps
/// <see cref="BlackspotsGrid.Compute"/> with the worker-level glue:
/// session-scoped tile preload, ground-elevation sampling, params
/// derivation, and a single-active-compute progress readout.
/// </summary>
internal sealed class BlackspotsCompute
{
    /// <summary>Hard cap matching the env validator (AppOptionsBinder.cs).</summary>
    private const double MaxRadiusKm = 1000.0;

    private readonly AppOptions _options;
    private readonly SrtmTileStore _tiles;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _computeGate = new(1, 1);
    private readonly SemaphoreSlim _preloadGate = new(1, 1);

    // Per-active-session state — cleared by ResetSession on idle eviction.
    private double? _groundElevM;
    private int _tileCount;

    private readonly object _progressGate = new();
    private int? _activeAltitudeKey;
    private double _activeProgress;

    public BlackspotsCompute(
        AppOptions options, SrtmTileStore tiles, TimeProvider time, ILogger logger)
    {
        _options = options;
        _tiles = tiles;
        _time = time;
        _logger = logger;
    }

    /// <summary>True when the SRTM tiles + ground elevation for this session
    /// are loaded in memory. Used by idle-eviction to short-circuit the
    /// "already evicted" case.</summary>
    public bool IsSessionLoaded => _groundElevM is not null;

    public Task WaitExclusiveAsync(CancellationToken ct) => _computeGate.WaitAsync(ct);
    public bool TryWaitExclusive() => _computeGate.Wait(0);
    public void ReleaseExclusive() => _computeGate.Release();

    /// <summary>Drop the per-session SRTM-tile + ground-elevation state.
    /// Caller must hold the compute gate (via
    /// <see cref="TryWaitExclusive"/>).</summary>
    public void ResetSession()
    {
        _groundElevM = null;
        _tileCount = 0;
    }

    /// <summary>Live progress snapshot for the compute currently running at
    /// <paramref name="targetAltM"/>. Returns <c>(false, 0)</c> when that
    /// altitude is cached, queued behind a different compute, or the
    /// feature is idle.</summary>
    public (bool Active, double Progress) GetProgress(double targetAltM)
    {
        var key = AltitudeKey(targetAltM);
        lock (_progressGate)
        {
            return _activeAltitudeKey == key ? (true, _activeProgress) : (false, 0.0);
        }
    }

    /// <summary>
    /// Run a fresh compute at the given altitude. Caller must hold the
    /// compute gate. Returns null if the compute is cancelled or the
    /// preload couldn't sample ground elevation.
    /// </summary>
    public async Task<BlackspotsGrid?> ComputeAtAsync(double targetAltM, CancellationToken ct)
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
    /// Compute the high-resolution "blocking face" raster — the per-pixel
    /// companion to the coarse cell grid.
    /// </summary>
    public async Task<BlockerFaceRaster?> ComputeFaceAsync(double targetAltM, CancellationToken ct)
    {
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
        var antennaMslM = ResolveAntennaMslM(groundElev);
        var faceParams = new BlockerFaceParams(
            ReceiverLat: _options.LatRef!.Value,
            ReceiverLon: _options.LonRef!.Value,
            AntennaMslM: antennaMslM,
            TargetAltitudeM: targetAltM,
            RadiusKm: EffectiveRadiusKm(antennaMslM, targetAltM, groundElev),
            GridDeg: _options.BlackspotsFaceGridDeg);
        _logger.LogInformation(
            "blackspots: computing face raster — antenna {Ant:F0} m MSL, target {Alt:F0} m MSL",
            faceParams.AntennaMslM, faceParams.TargetAltitudeM);
        return await Task.Run(
            () => BlockerFaceCompute.Compute(faceParams, _tiles), ct);
    }

    /// <summary>Re-derive a persisted grid's params at the live config and
    /// compare for full-record equality. Mismatch ⇒ user changed antenna /
    /// radius / grid / max-AGL config since the grid was written.</summary>
    public bool MatchesLiveConfig(BlackspotsParams persisted) =>
        persisted == BuildParams(persisted.TargetAltitudeM, persisted.GroundElevationM);

    /// <summary>
    /// Per-altitude grid radius. Floors at the operator-configured
    /// <see cref="AppOptions.BlackspotsRadiusKm"/> and grows up to the
    /// radio horizon for this antenna / target pair so the
    /// unreachable-cells ring always closes — at high target altitudes
    /// the curvature horizon would otherwise fall outside a fixed radius
    /// and leave an open arc. The 1.05 margin guarantees at least one
    /// row of definitely-unreachable cells outside the horizon. Capped
    /// at <see cref="MaxRadiusKm"/> (= the env validator's hard ceiling)
    /// so a stratospheric target doesn't blow the compute up to absurd
    /// sizes.
    /// </summary>
    public double EffectiveRadiusKm(double antennaMslM, double targetAltM, double groundElevM)
    {
        var horizonM = GreatCircle.RadioHorizonDistanceMetres(
            antennaHeightAglM: antennaMslM - groundElevM,
            targetHeightAglM: targetAltM - groundElevM);
        var horizonKm = horizonM / 1000.0 * 1.05;
        return Math.Clamp(
            Math.Max(_options.BlackspotsRadiusKm, horizonKm),
            _options.BlackspotsRadiusKm, MaxRadiusKm);
    }

    /// <summary>
    /// Resolve the antenna tip MSL from the configured options. If
    /// <see cref="AppOptions.BlackspotsAntennaMslM"/> is set, use it
    /// directly (user-measured values are trusted over the DEM's guess).
    /// Otherwise derive from the AGL fallback + the DEM-sampled ground
    /// elevation.
    /// </summary>
    private double ResolveAntennaMslM(double groundElevM) =>
        _options.BlackspotsAntennaMslM ?? (groundElevM + _options.BlackspotsAntennaAglM);

    private BlackspotsParams BuildParams(double targetAltM, double groundElevM)
    {
        var antennaMslM = ResolveAntennaMslM(groundElevM);
        return new(
            ReceiverLat: _options.LatRef!.Value,
            ReceiverLon: _options.LonRef!.Value,
            GroundElevationM: groundElevM,
            AntennaMslM: antennaMslM,
            TargetAltitudeM: targetAltM,
            RadiusKm: EffectiveRadiusKm(antennaMslM, targetAltM, groundElevM),
            GridDeg: _options.BlackspotsGridDeg,
            MaxAglM: _options.BlackspotsMaxAglM);
    }

    private static int AltitudeKey(double altM) => (int)Math.Round(altM);

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
    /// Download / load the SRTM tiles covering the receiver's bbox and
    /// sample the receiver's ground elevation. Idempotent — after the
    /// first successful call <see cref="_groundElevM"/> is set and this
    /// is a no-op.
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
}
