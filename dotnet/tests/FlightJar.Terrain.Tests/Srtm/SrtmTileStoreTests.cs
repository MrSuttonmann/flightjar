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
}
