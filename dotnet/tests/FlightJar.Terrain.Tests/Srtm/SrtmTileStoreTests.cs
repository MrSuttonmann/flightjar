using System.Buffers.Binary;
using System.IO.Compression;
using FlightJar.Terrain.Srtm;

namespace FlightJar.Terrain.Tests.Srtm;

public class SrtmTileStoreTests
{
    /// <summary>Write a 1-row-ramp tile gzipped to disk so EnsureLoadedAsync
    /// can pick it up without hitting the network.</summary>
    private static async Task SeedTileOnDiskAsync(string cacheDir, SrtmTileKey key)
    {
        Directory.CreateDirectory(cacheDir);
        var buf = new byte[SrtmTile.Size * SrtmTile.Size * 2];
        // Constant-elevation tile is plenty for these tests.
        for (var i = 0; i < buf.Length; i += 2)
        {
            BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(i, 2), 100);
        }
        var path = Path.Combine(cacheDir, key.Name + ".hgt.gz");
        await using var file = File.Create(path);
        await using var gz = new GZipStream(file, CompressionLevel.Fastest);
        await gz.WriteAsync(buf);
    }

    [Fact]
    public async Task EvictAll_DropsLoadedTiles_LeavesDiskCacheIntact()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"srtm-evict-{Guid.NewGuid():N}");
        try
        {
            var key = new SrtmTileKey(52, -2);
            await SeedTileOnDiskAsync(cacheDir, key);

            // No HttpClient call should ever happen — disk-cached tile
            // satisfies EnsureLoadedAsync entirely.
            var store = new SrtmTileStore(new HttpClient(), cacheDir);
            await store.EnsureLoadedAsync(new[] { key });

            Assert.Equal(1, store.LoadedCount);
            Assert.NotNull(store.TryGet(key));

            store.EvictAll();

            Assert.Equal(0, store.LoadedCount);
            Assert.Null(store.TryGet(key));
            // Disk cache must survive the eviction so the next load is
            // a disk read, not a download.
            Assert.True(File.Exists(Path.Combine(cacheDir, key.Name + ".hgt.gz")));

            // Re-loading after eviction works without network.
            await store.EnsureLoadedAsync(new[] { key });
            Assert.Equal(1, store.LoadedCount);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void EvictAll_OnEmptyStore_NoOp()
    {
        var store = new SrtmTileStore(new HttpClient(), Path.GetTempPath());
        Assert.Equal(0, store.LoadedCount);
        store.EvictAll();
        Assert.Equal(0, store.LoadedCount);
    }

    [Fact]
    public async Task EnsureLoadedAsync_FiresProgressCallback_PerTile()
    {
        // Caller drives the blackspots progress UI off this — the callback
        // must fire at least once per requested tile so the spinner doesn't
        // sit at 0 % through a multi-tile preload.
        var cacheDir = Path.Combine(Path.GetTempPath(), $"srtm-progress-{Guid.NewGuid():N}");
        try
        {
            var keys = new[]
            {
                new SrtmTileKey(52, -2),
                new SrtmTileKey(52, -1),
                new SrtmTileKey(53, -2),
            };
            foreach (var k in keys) await SeedTileOnDiskAsync(cacheDir, k);
            var store = new SrtmTileStore(new HttpClient(), cacheDir);

            var ticks = new List<(int Loaded, int Total)>();
            var ticksGate = new object();
            await store.EnsureLoadedAsync(
                keys,
                onTileLoaded: (loaded, total) =>
                {
                    lock (ticksGate) ticks.Add((loaded, total));
                });

            Assert.Equal(3, store.LoadedCount);
            // Final tick must report fully done so the bar can pin to 100 %
            // before the caller hands off to the next phase.
            Assert.Contains((3, 3), ticks);
            // Total stays consistent across every tick.
            Assert.All(ticks, t => Assert.Equal(3, t.Total));
            // Loaded count is non-decreasing (concurrent loads can interleave,
            // but no tick reports fewer tiles than a previous one).
            var prev = -1;
            foreach (var (loaded, _) in ticks)
            {
                Assert.True(loaded >= prev, $"loaded count went backwards: {prev} → {loaded}");
                prev = loaded;
            }
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
