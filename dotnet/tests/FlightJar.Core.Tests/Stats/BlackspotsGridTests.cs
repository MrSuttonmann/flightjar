using FlightJar.Core.Stats;
using FlightJar.Terrain;

namespace FlightJar.Core.Tests.Stats;

public class BlackspotsGridTests
{
    /// <summary>Uniform sea-level sampler.</summary>
    private sealed class FlatSampler : ITerrainSampler
    {
        public double ElevationMetres(double lat, double lon) => 0.0;
    }

    private static BlackspotsParams DefaultParams(
        double radiusKm = 50,
        double gridDeg = 0.1,
        double targetAltMslM = 10_000,
        double antennaMsl = 5,
        double maxAgl = 100) =>
        new(
            ReceiverLat: 51.5,
            ReceiverLon: -0.1,
            GroundElevationM: 0,
            AntennaMslM: antennaMsl,
            TargetAltitudeM: targetAltMslM,
            RadiusKm: radiusKm,
            GridDeg: gridDeg,
            MaxAglM: maxAgl);

    [Fact]
    public void Flat_terrain_high_target_produces_no_blackspots_short_range()
    {
        // 50 km radius, target at 10 km MSL — far above the earth-bulge horizon
        // at this range, so flat terrain should leave the grid empty.
        var grid = BlackspotsGrid.Compute(DefaultParams(), new FlatSampler());
        Assert.Empty(grid.Cells);
    }

    [Fact]
    public void Flat_terrain_low_target_long_range_gets_unreachable_cells_at_the_edge()
    {
        // 300 km radius, target at only 500 m MSL. Earth-bulge drops the target
        // well below the geometric horizon past ~100 km, and raising the
        // antenna another 100 m doesn't help recover a target that's 3+ km
        // below the horizon — so outer cells come back "unreachable".
        var p = DefaultParams(radiusKm: 300, gridDeg: 0.2, targetAltMslM: 500);
        var grid = BlackspotsGrid.Compute(p, new FlatSampler());
        Assert.NotEmpty(grid.Cells);
        Assert.Contains(grid.Cells, c => c.RequiredAntennaMslM is null);
    }

    [Fact]
    public void Compute_records_exact_params()
    {
        var p = DefaultParams();
        var grid = BlackspotsGrid.Compute(p, new FlatSampler());
        Assert.Equal(p, grid.Params);
    }

    [Fact]
    public async Task Save_and_load_roundtrip_preserves_every_altitude()
    {
        var gridA = BlackspotsGrid.Compute(
            DefaultParams(radiusKm: 300, gridDeg: 0.2, targetAltMslM: 500), new FlatSampler());
        var gridB = BlackspotsGrid.Compute(
            DefaultParams(radiusKm: 300, gridDeg: 0.2, targetAltMslM: 3000), new FlatSampler());
        var path = Path.Combine(Path.GetTempPath(), $"blackspots-test-{Guid.NewGuid():N}.json.gz");
        try
        {
            await BlackspotsGrid.SaveAllAsync(path, new[] { gridA, gridB });
            var loaded = await BlackspotsGrid.LoadAllAsync(path);
            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, g => g.Params.TargetAltitudeM == 500);
            Assert.Contains(loaded, g => g.Params.TargetAltitudeM == 3000);
            foreach (var expected in new[] { gridA, gridB })
            {
                var actual = loaded.First(g => g.Params.TargetAltitudeM == expected.Params.TargetAltitudeM);
                Assert.Equal(expected.Params, actual.Params);
                Assert.Equal(expected.Cells.Count, actual.Cells.Count);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Load_returns_empty_when_file_missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-blackspots-{Guid.NewGuid():N}.json.gz");
        var loaded = await BlackspotsGrid.LoadAllAsync(missing);
        Assert.Empty(loaded);
    }

    [Fact]
    public void IsValid_reflects_tile_coverage()
    {
        var p = DefaultParams();
        // Single-tile bboxes are trivially fine — no way to tell whether
        // missing elevation is a download failure or genuinely empty ocean.
        var singleTile = new BlackspotsGrid(p, DateTimeOffset.UtcNow, tileCount: 1, tilesWithData: 0, cells: Array.Empty<BlackspotCell>());
        Assert.True(singleTile.IsValid);

        // Multi-tile bbox with only one tile returning data: the smoking gun
        // for blocked downloads. Reject.
        var degenerate = new BlackspotsGrid(p, DateTimeOffset.UtcNow, tileCount: 25, tilesWithData: 1, cells: Array.Empty<BlackspotCell>());
        Assert.False(degenerate.IsValid);

        // Healthy multi-tile bbox passes.
        var healthy = new BlackspotsGrid(p, DateTimeOffset.UtcNow, tileCount: 25, tilesWithData: 18, cells: Array.Empty<BlackspotCell>());
        Assert.True(healthy.IsValid);
    }

    [Fact]
    public void Progress_callback_fires_monotonically_and_reaches_one()
    {
        var p = DefaultParams(radiusKm: 300, gridDeg: 0.2, targetAltMslM: 500);
        var observed = new System.Collections.Concurrent.ConcurrentQueue<double>();
        BlackspotsGrid.Compute(p, new FlatSampler(), onProgress: observed.Enqueue);

        var values = observed.ToArray();
        Assert.NotEmpty(values);
        // Final call must be exactly 1.0 — that's the done==totalCells branch.
        Assert.Equal(1.0, values[^1], 6);
        // Each value must be in (0, 1]. No NaN, no negatives, no overshoot.
        Assert.All(values, v => Assert.InRange(v, 0.001, 1.0));
        // We throttle at 2 %, so across a full compute we expect roughly
        // 50 calls — fewer than ~30 means the worker fires too sparsely
        // for a 300 ms-poll frontend to show meaningful movement.
        Assert.InRange(values.Length, 30, 60);
        // And values must be strictly non-decreasing — otherwise the
        // frontend's %-readout can jump backwards.
        for (var i = 1; i < values.Length; i++)
        {
            Assert.True(values[i] >= values[i - 1],
                $"progress went backwards: {values[i - 1]} -> {values[i]} at index {i}");
        }
    }

    [Fact]
    public void BboxFor_widens_longitudinally_at_high_latitude()
    {
        var lowLat = BlackspotsGrid.BboxFor(0, 0, 111.32);
        var highLat = BlackspotsGrid.BboxFor(60, 0, 111.32);
        var lowLon = lowLat.MaxLon - lowLat.MinLon;
        var highLon = highLat.MaxLon - highLat.MinLon;
        // At 60°N the east-west degree is half what it is at the equator, so
        // the bbox has to be ~2× wider in longitude to cover the same range.
        Assert.InRange(highLon / lowLon, 1.8, 2.2);
    }
}
