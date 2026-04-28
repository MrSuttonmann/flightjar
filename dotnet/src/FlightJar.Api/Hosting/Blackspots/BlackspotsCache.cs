using FlightJar.Core.Stats;

namespace FlightJar.Api.Hosting.Blackspots;

/// <summary>
/// In-memory LRU + face-raster cache + on-disk gzipped-JSON persistence
/// for blackspots grids. Owned by <see cref="BlackspotsWorker"/>; survives
/// idle-eviction at the disk layer (the in-memory side is wiped by
/// <see cref="Clear"/>).
/// </summary>
internal sealed class BlackspotsCache
{
    private const int MaxCachedAltitudes = 8;

    private readonly object _gate = new();
    private readonly LinkedList<BlackspotsGrid> _order = new();
    private readonly Dictionary<int, LinkedListNode<BlackspotsGrid>> _nodes = new();
    private readonly Dictionary<int, BlockerFaceRaster> _faceCache = new();

    private readonly string? _persistPath;
    private readonly ILogger _logger;

    /// <summary>Has the persisted-grid file been read into the LRU cache yet?
    /// First <see cref="TryClaimPersistedLoad"/> flips this so the load
    /// happens lazily rather than on startup. Reset by
    /// <see cref="ResetPersistedLoaded"/> after eviction so the next
    /// request re-reads from disk.</summary>
    private int _persistedLoaded;

    public BlackspotsCache(string? persistPath, ILogger logger)
    {
        _persistPath = persistPath;
        _logger = logger;
    }

    public BlackspotsGrid? TryGet(int altKey)
    {
        lock (_gate)
        {
            if (_nodes.TryGetValue(altKey, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value;
            }
            return null;
        }
    }

    public void Insert(int altKey, BlackspotsGrid grid)
    {
        lock (_gate)
        {
            if (_nodes.TryGetValue(altKey, out var existing))
            {
                _order.Remove(existing);
                _nodes.Remove(altKey);
            }
            var node = _order.AddFirst(grid);
            _nodes[altKey] = node;
            while (_order.Count > MaxCachedAltitudes)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _nodes.Remove((int)Math.Round(last.Value.Params.TargetAltitudeM));
            }
        }
    }

    public BlockerFaceRaster? TryGetFace(int altKey)
    {
        lock (_gate)
        {
            return _faceCache.TryGetValue(altKey, out var raster) ? raster : null;
        }
    }

    public void InsertFace(int altKey, BlockerFaceRaster raster)
    {
        lock (_gate)
        {
            _faceCache[altKey] = raster;
        }
    }

    /// <summary>Wipe the in-memory LRU + face cache. Called by idle-eviction.
    /// Disk persistence is untouched.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _order.Clear();
            _nodes.Clear();
            _faceCache.Clear();
        }
    }

    /// <summary>
    /// Atomic claim of "this caller is the first to load persisted grids".
    /// Returns true exactly once per eviction cycle; subsequent calls
    /// return false until <see cref="ResetPersistedLoaded"/> runs.
    /// </summary>
    public bool TryClaimPersistedLoad() =>
        Interlocked.CompareExchange(ref _persistedLoaded, 1, 0) == 0;

    public void ResetPersistedLoaded() =>
        Interlocked.Exchange(ref _persistedLoaded, 0);

    /// <summary>
    /// Re-hydrate the LRU grid cache from <c>blackspots.json.gz</c>.
    /// Mismatched-config + invalid grids are dropped silently; persisted
    /// grids that survive the filter just shortcut the next compute for
    /// their altitude.
    /// </summary>
    public async Task LoadPersistedAsync(
        Func<BlackspotsParams, bool> matchesLiveConfig, CancellationToken ct)
    {
        if (_persistPath is null) return;

        var loaded = await BlackspotsGrid.LoadAllAsync(_persistPath, _logger, ct);
        if (loaded.Count == 0)
        {
            return;
        }
        var kept = 0;
        foreach (var grid in loaded)
        {
            if (!matchesLiveConfig(grid.Params)) continue;
            if (!grid.IsValid) continue;
            Insert((int)Math.Round(grid.Params.TargetAltitudeM), grid);
            kept++;
        }
        _logger.LogInformation(
            "blackspots: hydrated {Kept}/{Total} persisted grid(s) on first request",
            kept, loaded.Count);
    }

    /// <summary>
    /// Write every valid LRU entry to disk. Invalid grids (the "perfect
    /// circle" signal) are omitted so a single bad network session can't
    /// poison future restarts.
    /// </summary>
    public async Task PersistAsync(CancellationToken ct)
    {
        if (_persistPath is null)
        {
            return;
        }
        List<BlackspotsGrid> toSave;
        lock (_gate)
        {
            toSave = _order.Where(g => g.IsValid).ToList();
        }
        if (toSave.Count == 0)
        {
            return;
        }
        await BlackspotsGrid.SaveAllAsync(_persistPath, toSave, _logger, ct);
    }
}
