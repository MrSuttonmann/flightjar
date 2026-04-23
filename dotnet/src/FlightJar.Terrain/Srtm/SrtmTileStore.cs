using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlightJar.Terrain.Srtm;

/// <summary>
/// On-demand SRTM1 tile cache. Tiles are fetched from AWS's <c>elevation-tiles-prod</c>
/// public bucket (skadi layout), gunzipped, and cached to disk as <c>.hgt.gz</c>
/// files (one per tile, no metadata sidecar needed — the filename carries the key).
/// A 404 from the upstream is cached as a permanent "ocean / void" tile so
/// repeated queries over open water don't re-hit the network.
/// </summary>
public sealed class SrtmTileStore : ITerrainSampler
{
    private const string DefaultUrlTemplate =
        "https://elevation-tiles-prod.s3.amazonaws.com/skadi/{0}/{1}.hgt.gz";

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly string _urlTemplate;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<SrtmTileKey, SrtmTile> _loaded = new();
    private readonly ConcurrentDictionary<SrtmTileKey, Task<SrtmTile>> _inflight = new();

    public SrtmTileStore(
        HttpClient http,
        string cacheDir,
        ILogger<SrtmTileStore>? logger = null,
        string? urlTemplate = null)
    {
        _http = http;
        _cacheDir = cacheDir;
        _logger = logger ?? NullLogger<SrtmTileStore>.Instance;
        _urlTemplate = urlTemplate ?? DefaultUrlTemplate;
    }

    /// <summary>Already-loaded tile, or null if not present. Does not trigger a fetch.</summary>
    public SrtmTile? TryGet(SrtmTileKey key) =>
        _loaded.TryGetValue(key, out var t) ? t : null;

    /// <summary>
    /// Snapshot of per-tile load state. Used by the worker to warn when most
    /// tiles came back empty — a strong signal that downloads are being
    /// blocked by a firewall / proxy and the resulting "perfect circle"
    /// blackspot output is not what the user should be looking at.
    /// </summary>
    public (int Loaded, int Empty) TileLoadSummary()
    {
        var empty = 0;
        foreach (var kv in _loaded)
        {
            if (kv.Value.IsEmpty) empty++;
        }
        return (_loaded.Count, empty);
    }

    /// <inheritdoc />
    public double ElevationMetres(double lat, double lon)
    {
        var key = SrtmTileKey.Containing(lat, lon);
        return _loaded.TryGetValue(key, out var tile) ? tile.Sample(lat, lon) : 0.0;
    }

    /// <summary>
    /// Ensure every tile in <paramref name="keys"/> is loaded into memory. Tiles
    /// already in memory are a no-op; missing tiles are read from disk cache
    /// when present, otherwise downloaded. Runs with a small concurrency cap so
    /// we don't open a hundred connections at once.
    /// </summary>
    public async Task EnsureLoadedAsync(
        IEnumerable<SrtmTileKey> keys,
        int concurrency = 4,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = new List<Task>();
        foreach (var key in keys.Distinct())
        {
            if (_loaded.ContainsKey(key))
            {
                continue;
            }
            tasks.Add(EnsureOneAsync(key, gate, ct));
        }
        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    private async Task EnsureOneAsync(SrtmTileKey key, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var task = _inflight.GetOrAdd(key, k => LoadAsync(k, ct));
            var tile = await task;
            _loaded[key] = tile;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
            gate.Release();
        }
    }

    private async Task<SrtmTile> LoadAsync(SrtmTileKey key, CancellationToken ct)
    {
        // Disk cache first.
        var diskPath = Path.Combine(_cacheDir, key.Name + ".hgt.gz");
        if (File.Exists(diskPath))
        {
            try
            {
                return await ReadLocalAsync(key, diskPath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "corrupt SRTM tile at {Path} — refetching", diskPath);
                try { File.Delete(diskPath); } catch { /* best-effort */ }
            }
        }
        // Not cached — fetch.
        var url = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            _urlTemplate,
            key.Name[..3],  // "N52" or "S33"
            key.Name);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Ocean / no-data tile — persist an empty sentinel marker so we
                // don't retry on the next startup. We use a zero-byte file; the
                // load path treats it as "parse failed → empty tile".
                _logger.LogDebug("SRTM tile {Tile} not available (ocean / no data)", key);
                Directory.CreateDirectory(_cacheDir);
                await File.WriteAllBytesAsync(diskPath, [], ct);
                return SrtmTile.Empty(key);
            }
            response.EnsureSuccessStatusCode();
            Directory.CreateDirectory(_cacheDir);
            // Stream upstream body straight to disk (gzip-compressed on the wire).
            await using (var netStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(diskPath))
            {
                await netStream.CopyToAsync(file, ct);
            }
            _logger.LogInformation("fetched SRTM tile {Tile} ({Bytes:N0} B on disk)",
                key, new FileInfo(diskPath).Length);
            return await ReadLocalAsync(key, diskPath, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "couldn't fetch SRTM tile {Tile}", key);
            return SrtmTile.Empty(key);
        }
    }

    private static async Task<SrtmTile> ReadLocalAsync(SrtmTileKey key, string path, CancellationToken ct)
    {
        var fi = new FileInfo(path);
        if (fi.Length == 0)
        {
            // Cached "ocean" sentinel.
            return SrtmTile.Empty(key);
        }
        await using var file = File.OpenRead(path);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var buffer = new MemoryStream(SrtmTile.Size * SrtmTile.Size * 2);
        await gz.CopyToAsync(buffer, ct);
        return SrtmTile.FromBytes(key, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>
    /// Return the set of tile keys whose 1°×1° cells are touched by the
    /// axis-aligned bounding box [minLat, maxLat] × [minLon, maxLon].
    /// </summary>
    public static IEnumerable<SrtmTileKey> TilesForBbox(double minLat, double maxLat, double minLon, double maxLon)
    {
        var latLo = (int)Math.Floor(minLat);
        var latHi = (int)Math.Floor(maxLat);
        var lonLo = (int)Math.Floor(minLon);
        var lonHi = (int)Math.Floor(maxLon);
        for (var la = latLo; la <= latHi; la++)
        {
            for (var lo = lonLo; lo <= lonHi; lo++)
            {
                yield return new SrtmTileKey(la, lo);
            }
        }
    }
}
