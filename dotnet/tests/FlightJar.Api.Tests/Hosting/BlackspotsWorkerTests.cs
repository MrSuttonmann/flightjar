using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using FlightJar.Api.Hosting;
using FlightJar.Core.Configuration;
using FlightJar.Terrain.Srtm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightJar.Api.Tests.Hosting;

/// <summary>
/// Idle-eviction tests for BlackspotsWorker. The full compute path runs
/// the LOS solver (slow + needs realistic SRTM data), so these tests
/// focus on the lifecycle: no-startup-load, lazy first hydration, and
/// the idle sweep evicting SRTM tiles + grids when activity stops.
/// </summary>
public class BlackspotsWorkerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static AppOptions Options(double idleTimeoutMin = 15.0) => new()
    {
        BlackspotsEnabled = true,
        LatRef = 52.98,
        LonRef = -1.20,
        BlackspotsRadiusKm = 50.0,           // small radius → 1 SRTM tile
        BlackspotsGridDeg = 0.5,
        BlackspotsMaxAglM = 100.0,
        BlackspotsAntennaAglM = 5.0,
        BlackspotsIdleTimeoutMinutes = idleTimeoutMin,
    };

    private static async Task<string> SeedTileOnDiskAsync(SrtmTileKey key)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bs-evict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var buf = new byte[SrtmTile.Size * SrtmTile.Size * 2];
        for (var i = 0; i < buf.Length; i += 2)
        {
            BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(i, 2), 50);
        }
        var path = Path.Combine(dir, key.Name + ".hgt.gz");
        await using var file = File.Create(path);
        await using var gz = new GZipStream(file, CompressionLevel.Fastest);
        await gz.WriteAsync(buf);
        return dir;
    }

    private static void StampLastAccess(BlackspotsWorker worker, DateTimeOffset ts)
    {
        var field = typeof(BlackspotsWorker).GetField(
            "_lastAccessTicks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // _lastAccessTicks is a long stored via Interlocked. Reflection
        // SetValue is fine here — single-threaded test, no concurrent
        // writers to race with.
        field.SetValue(worker, ts.UtcTicks);
    }

    [Fact]
    public void EvictIfIdle_NeverAccessed_ReturnsFalse()
    {
        // Cold start: nothing loaded, nothing to evict, nothing to do.
        // The sweep should be a no-op without errors.
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var tiles = new SrtmTileStore(new HttpClient(), dir);
        var time = new FakeTimeProvider(T0);
        var worker = new BlackspotsWorker(
            Options(), tiles, time,
            NullLogger<BlackspotsWorker>.Instance, persistPath: null);

        Assert.False(worker.EvictIfIdle(TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void EvictIfIdle_WithinTimeout_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var tiles = new SrtmTileStore(new HttpClient(), dir);
        var time = new FakeTimeProvider(T0);
        var worker = new BlackspotsWorker(
            Options(), tiles, time,
            NullLogger<BlackspotsWorker>.Instance, persistPath: null);

        StampLastAccess(worker, T0);
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.False(worker.EvictIfIdle(TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task EvictIfIdle_PastTimeout_DropsTilesAndGrids()
    {
        var key = new SrtmTileKey(52, -2);
        var dir = await SeedTileOnDiskAsync(key);
        try
        {
            var tiles = new SrtmTileStore(new HttpClient(), dir);
            await tiles.EnsureLoadedAsync(new[] { key });
            Assert.Equal(1, tiles.LoadedCount);

            var time = new FakeTimeProvider(T0);
            var worker = new BlackspotsWorker(
                Options(), tiles, time,
                NullLogger<BlackspotsWorker>.Instance, persistPath: null);

            StampLastAccess(worker, T0);
            time.Advance(TimeSpan.FromMinutes(20));

            // Set _groundElevM so EvictIfIdle has something to clear
            // (otherwise it short-circuits as "already evicted"). The
            // field moved onto BlackspotsCompute as part of the M2 split,
            // so reach it via the worker's `_compute` field.
            var computeField = typeof(BlackspotsWorker).GetField(
                "_compute", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var compute = computeField.GetValue(worker)!;
            compute.GetType()
                .GetField("_groundElevM", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(compute, 50.0);

            Assert.True(worker.EvictIfIdle(TimeSpan.FromMinutes(15)));

            // SRTM tiles dropped from memory.
            Assert.Equal(0, tiles.LoadedCount);
            // Disk cache survives so re-load is cheap.
            Assert.True(File.Exists(Path.Combine(dir, key.Name + ".hgt.gz")));
            // Subsequent sweep is a no-op (timer reset to "never accessed").
            Assert.False(worker.EvictIfIdle(TimeSpan.FromMinutes(15)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void EvictIfIdle_ZeroTimeout_NeverEvicts()
    {
        // BlackspotsIdleTimeoutMinutes=0 disables eviction entirely.
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var tiles = new SrtmTileStore(new HttpClient(), dir);
        var time = new FakeTimeProvider(T0);
        var worker = new BlackspotsWorker(
            Options(idleTimeoutMin: 0), tiles, time,
            NullLogger<BlackspotsWorker>.Instance, persistPath: null);

        StampLastAccess(worker, T0);
        time.Advance(TimeSpan.FromHours(24));

        Assert.False(worker.EvictIfIdle(TimeSpan.Zero));
    }

    private static BlackspotsWorker NewWorkerForRadius(AppOptions options)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var tiles = new SrtmTileStore(new HttpClient(), dir);
        return new BlackspotsWorker(
            options, tiles, new FakeTimeProvider(T0),
            NullLogger<BlackspotsWorker>.Instance, persistPath: null);
    }

    [Fact]
    public void EffectiveRadiusKm_LowAltitudeWithinFloor_ReturnsConfiguredFloor()
    {
        // FL050 (1524 m) target → curvature horizon ≈ 161 km, well inside the
        // 200 km configured floor. The floor must win so we don't shrink the
        // grid below what the operator asked for.
        var opts = Options() with { BlackspotsRadiusKm = 200.0 };
        var worker = NewWorkerForRadius(opts);
        var r = worker.EffectiveRadiusKm(antennaMslM: 50, targetAltM: 1524, groundElevM: 50);
        Assert.Equal(200.0, r, 6);
    }

    [Fact]
    public void EffectiveRadiusKm_HighAltitudeBeyondFloor_ExtendsToHorizon()
    {
        // FL400 (12 192 m) target with a sea-level antenna → horizon ≈ 454 km.
        // With a 200 km configured floor the grid must extend past the floor
        // to ~horizon × 1.05 so the unreachable-cells ring closes.
        var opts = Options() with { BlackspotsRadiusKm = 200.0 };
        var worker = NewWorkerForRadius(opts);
        var r = worker.EffectiveRadiusKm(antennaMslM: 0, targetAltM: 12192, groundElevM: 0);
        Assert.InRange(r, 470.0, 500.0); // ~454 × 1.05 = 477
    }

    [Fact]
    public void EffectiveRadiusKm_StratosphericTarget_CapsAt1000km()
    {
        // An 80 km MSL target gives horizon × 1.05 ≈ 1224 km — the env
        // validator hard-caps the operator-configured radius at 1000 km,
        // and the auto-extension must respect the same ceiling.
        var opts = Options() with { BlackspotsRadiusKm = 100.0 };
        var worker = NewWorkerForRadius(opts);
        var r = worker.EffectiveRadiusKm(antennaMslM: 0, targetAltM: 80_000, groundElevM: 0);
        Assert.Equal(1000.0, r, 6);
    }
}
