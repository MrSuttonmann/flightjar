using FlightJar.Terrain.LineOfSight;

namespace FlightJar.Terrain.Tests.LineOfSight;

public class GreatCircleTests
{
    [Fact]
    public void DistanceMetres_pole_to_equator_is_quarter_circumference()
    {
        var d = GreatCircle.DistanceMetres(0, 0, 90, 0);
        var expected = Math.PI / 2 * GreatCircle.EarthRadiusMetres;
        Assert.Equal(expected, d, 1.0);
    }

    [Fact]
    public void DistanceMetres_same_point_is_zero()
    {
        Assert.Equal(0, GreatCircle.DistanceMetres(52.98, -1.20, 52.98, -1.20), 6);
    }

    [Fact]
    public void DistanceMetres_one_degree_of_longitude_at_equator()
    {
        // 1° of longitude at the equator ≈ πR/180 ≈ 111.195 km.
        var d = GreatCircle.DistanceMetres(0, 0, 0, 1);
        var expected = Math.PI * GreatCircle.EarthRadiusMetres / 180.0;
        Assert.Equal(expected, d, 1.0);
    }

    [Fact]
    public void InitialBearing_due_north_is_zero()
    {
        var b = GreatCircle.InitialBearingDeg(0, 0, 1, 0);
        Assert.Equal(0.0, b, 3);
    }

    [Fact]
    public void InitialBearing_due_east_on_equator_is_ninety()
    {
        var b = GreatCircle.InitialBearingDeg(0, 0, 0, 1);
        Assert.Equal(90.0, b, 3);
    }

    [Fact]
    public void Destination_roundtrips_with_bearing_and_distance()
    {
        var (lat1, lon1) = (52.0, -1.5);
        var (lat2, lon2) = (53.0, -0.5);
        var bearing = GreatCircle.InitialBearingDeg(lat1, lon1, lat2, lon2);
        var distance = GreatCircle.DistanceMetres(lat1, lon1, lat2, lon2);
        var (endLat, endLon) = GreatCircle.Destination(lat1, lon1, bearing, distance);
        Assert.Equal(lat2, endLat, 6);
        Assert.Equal(lon2, endLon, 6);
    }

    [Fact]
    public void EffectiveRadius_is_four_thirds_of_earth()
    {
        Assert.Equal(4.0 / 3.0 * GreatCircle.EarthRadiusMetres, GreatCircle.EffectiveRadiusMetres, 6);
    }

    [Fact]
    public void RadioHorizon_sea_level_antenna_FL100_target_is_about_227_km()
    {
        // sqrt(2 · 4/3 · 6_371_008.8 · 3048) ≈ 227.4 km. The closed-form is
        // exact for the chosen refraction model, so the tolerance here is
        // tight (5 m) — we're checking the formula, not the model.
        var d = GreatCircle.RadioHorizonDistanceMetres(0, 3048);
        var expected = Math.Sqrt(2.0 * GreatCircle.EffectiveRadiusMetres * 3048);
        Assert.Equal(expected, d, 1.0);
        Assert.InRange(d / 1000.0, 226.0, 228.0);
    }

    [Fact]
    public void RadioHorizon_adds_antenna_and_target_slices()
    {
        var antennaOnly = GreatCircle.RadioHorizonDistanceMetres(100, 0);
        var targetOnly = GreatCircle.RadioHorizonDistanceMetres(0, 3048);
        var combined = GreatCircle.RadioHorizonDistanceMetres(100, 3048);
        Assert.Equal(antennaOnly + targetOnly, combined, 1e-6);
    }

    [Fact]
    public void RadioHorizon_treats_negative_inputs_as_zero()
    {
        // Sub-ground antennas / targets can't see further than ground level —
        // the formula must clamp rather than blow up under sqrt of a negative.
        var d = GreatCircle.RadioHorizonDistanceMetres(-50, 3048);
        var expected = GreatCircle.RadioHorizonDistanceMetres(0, 3048);
        Assert.Equal(expected, d, 1e-6);
    }
}
