using FlightJar.Terrain;
using FlightJar.Terrain.LineOfSight;

namespace FlightJar.Terrain.Tests.LineOfSight;

public class LineOfSightSolverTests
{
    /// <summary>Uniform sea-level sampler: terrain is always zero metres.</summary>
    private sealed class FlatSampler : ITerrainSampler
    {
        public double ElevationMetres(double lat, double lon) => 0.0;
    }

    /// <summary>
    /// A single "wall" obstacle: within [minLat..maxLat] × [minLon..maxLon] the
    /// terrain is <paramref name="heightM"/> metres; elsewhere it's sea level.
    /// Lets us test an LOS pierce in closed form.
    /// </summary>
    private sealed class WallSampler(
        double minLat, double maxLat, double minLon, double maxLon, double heightM) : ITerrainSampler
    {
        public double ElevationMetres(double lat, double lon) =>
            (lat >= minLat && lat <= maxLat && lon >= minLon && lon <= maxLon) ? heightM : 0.0;
    }

    [Fact]
    public void Flat_terrain_is_clear_when_target_rises_above_earth_bulge()
    {
        // Flat sea-level terrain + antenna 10 m MSL + target at 10 km altitude
        // at 50 km range. Earth bulge at 50 km is ~147 m (with 4/3 refraction),
        // well below the target altitude — should be clear.
        var rx = new LosReceiver(52.0, -1.5, AntennaMslM: 10);
        var t = new LosTarget(52.45, -1.5, AltitudeMslM: 10_000);
        var result = LineOfSightSolver.Solve(rx, t, new FlatSampler());
        Assert.False(result.Blocked);
    }

    [Fact]
    public void Flat_terrain_long_range_low_target_is_blocked_by_earth_bulge()
    {
        // 300 km range, target at only 500 m MSL, antenna at 10 m MSL. Earth
        // bulge at 300 km is ~5.3 km — target dips well below the horizon, and
        // raising the antenna ceiling to 110 m MSL doesn't help recover a
        // target that's kilometres below the horizon.
        var rx = new LosReceiver(52.0, -1.5, AntennaMslM: 10);
        // 300 km north of the receiver along the meridian: lat ≈ 52 + 300/111 ≈ 54.7.
        var t = new LosTarget(54.7, -1.5, AltitudeMslM: 500);
        var result = LineOfSightSolver.Solve(rx, t, new FlatSampler(), ceilingMslM: 110);
        Assert.True(result.Blocked);
        Assert.Null(result.RequiredAntennaMslM);
    }

    [Fact]
    public void Wall_obstruction_blocks_then_clears_above_required_height()
    {
        // Receiver at sea level, target at 3 km, 40 km range. Put a wall of
        // 1500 m at the midpoint. Without enough antenna height we should clip
        // the wall; with enough we clear it.
        var rx = new LosReceiver(52.0, -1.5, AntennaMslM: 0);
        // ~40 km due north: lat ≈ 52.36
        var t = new LosTarget(52.36, -1.5, AltitudeMslM: 3_000);
        var midLat = (52.0 + 52.36) / 2;
        // Wall covers a small latitude band right at the midpoint so sampling
        // reliably hits it.
        var wall = new WallSampler(midLat - 0.005, midLat + 0.005, -2.0, -1.0, 1500);

        var blocked = LineOfSightSolver.Solve(rx, t, wall, ceilingMslM: 10_000);
        Assert.True(blocked.Blocked);
        Assert.NotNull(blocked.RequiredAntennaMslM);
        // Required antenna tip should be non-trivial but well below the wall's
        // 1500 m — the target is above the wall, so the line only has to clear
        // the wall partway along.
        Assert.InRange(blocked.RequiredAntennaMslM!.Value, 1, 1500);
    }

    [Fact]
    public void Same_point_query_is_not_blocked()
    {
        var rx = new LosReceiver(52.0, -1.5, AntennaMslM: 10);
        var t = new LosTarget(52.0, -1.5, AltitudeMslM: 1_000);
        var result = LineOfSightSolver.Solve(rx, t, new FlatSampler());
        Assert.False(result.Blocked);
    }

    [Fact]
    public void Unreachable_when_wall_too_tall_within_ceiling()
    {
        // Wall taller than the bisection ceiling → unreachable.
        var rx = new LosReceiver(52.0, -1.5, AntennaMslM: 0);
        var t = new LosTarget(52.36, -1.5, AltitudeMslM: 500);
        var wall = new WallSampler(52.0, 52.36, -2.0, -1.0, 5_000);
        var result = LineOfSightSolver.Solve(rx, t, wall, ceilingMslM: 100);
        Assert.True(result.Blocked);
        Assert.Null(result.RequiredAntennaMslM);
    }

    [Fact]
    public void Blocked_result_carries_obstruction_at_wall_location_and_height()
    {
        // Same setup as the wall-clears test above. The worst-offending sample
        // must land inside the wall's lat band (the only place terrain rises
        // above sea level) and report the wall's full 1500 m height.
        var rx = new LosReceiver(52.0, -1.5, AntennaMslM: 0);
        var t = new LosTarget(52.36, -1.5, AltitudeMslM: 3_000);
        var midLat = (52.0 + 52.36) / 2;
        var wall = new WallSampler(midLat - 0.005, midLat + 0.005, -2.0, -1.0, 1500);

        var result = LineOfSightSolver.Solve(rx, t, wall, ceilingMslM: 10_000);
        Assert.True(result.Blocked);
        Assert.NotNull(result.Obstruction);
        var ob = result.Obstruction!.Value;
        Assert.InRange(ob.Lat, midLat - 0.005, midLat + 0.005);
        // Bearing is due north so longitude should stay at -1.5 ± numerical noise.
        Assert.InRange(ob.Lon, -1.51, -1.49);
        Assert.Equal(1500, ob.ElevMslM, precision: 0);
    }

    [Fact]
    public void Clear_result_has_no_obstruction()
    {
        var rx = new LosReceiver(52.0, -1.5, AntennaMslM: 10);
        var t = new LosTarget(52.45, -1.5, AltitudeMslM: 10_000);
        var result = LineOfSightSolver.Solve(rx, t, new FlatSampler());
        Assert.False(result.Blocked);
        Assert.Null(result.Obstruction);
    }

    [Fact]
    public void Sea_level_only_blockage_reports_no_obstruction()
    {
        // Long-range, low-target case where only earth curvature blocks the
        // line — the "obstruction" along the path is sea-level terrain (SRTM
        // ocean / no-data / bathymetry). Cell still reads blocked, but no
        // phantom 0 m hill should be emitted into the blocker overlay.
        var rx = new LosReceiver(52.0, -1.5, AntennaMslM: 10);
        var t = new LosTarget(54.7, -1.5, AltitudeMslM: 500);
        var result = LineOfSightSolver.Solve(rx, t, new FlatSampler(), ceilingMslM: 110);
        Assert.True(result.Blocked);
        Assert.Null(result.Obstruction);
    }

    [Fact]
    public void Hidden_far_peak_loses_to_nearer_taller_silhouette()
    {
        // Two real hills along the path: a tall nearer ridge and a taller far
        // peak that's geometrically above the LOS line but hidden behind the
        // ridge from the receiver's perspective. The reported obstruction
        // must be the nearer ridge, not the far peak — under the old
        // max-overage logic, the far peak (with more elev above the line)
        // would have won despite being invisible from the antenna.
        var rx = new LosReceiver(52.0, -1.5, AntennaMslM: 0);
        // ~40 km due north target at low altitude so the line slopes gently
        // and both hills sit above it.
        var t = new LosTarget(52.36, -1.5, AltitudeMslM: 50);
        // Nearer ridge: 5 km out, 400 m tall. Angular = 0.08 rad.
        var nearLat = 52.0 + 5.0 / 111.0;
        var near = new WallSampler(nearLat - 0.005, nearLat + 0.005, -2.0, -1.0, 400);
        // Far peak: 30 km out, 1000 m tall. Angular = 0.033 rad — well below
        // the nearer ridge's silhouette, so RX physically can't see it.
        var farLat = 52.0 + 30.0 / 111.0;
        var far = new WallSampler(farLat - 0.005, farLat + 0.005, -2.0, -1.0, 1000);
        var sampler = new CombinedSampler(near, far);

        var result = LineOfSightSolver.Solve(rx, t, sampler, ceilingMslM: 10_000);
        Assert.True(result.Blocked);
        Assert.NotNull(result.Obstruction);
        var ob = result.Obstruction!.Value;
        // Must land on the nearer ridge's lat band, not the far peak's.
        Assert.InRange(ob.Lat, nearLat - 0.01, nearLat + 0.01);
        Assert.Equal(400, ob.ElevMslM, precision: 0);
    }

    /// <summary>Returns the max elevation across two underlying samplers.</summary>
    private sealed class CombinedSampler(ITerrainSampler a, ITerrainSampler b) : ITerrainSampler
    {
        public double ElevationMetres(double lat, double lon) =>
            Math.Max(a.ElevationMetres(lat, lon), b.ElevationMetres(lat, lon));
    }
}
