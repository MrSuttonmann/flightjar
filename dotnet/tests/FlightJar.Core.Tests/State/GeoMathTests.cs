using FlightJar.Core.State;

namespace FlightJar.Core.Tests.State;

public class GeoMathTests
{
    [Fact]
    public void Distance_SamePoint_IsZero()
    {
        Assert.Equal(0, GeoMath.ApproxDistanceKm(52.0, -1.0, 52.0, -1.0), 6);
    }

    [Fact]
    public void Distance_OneDegreeLatitudeIsRoughly111Km()
    {
        var d = GeoMath.ApproxDistanceKm(52.0, 0, 53.0, 0);
        Assert.InRange(d, 110, 112);
    }

    [Fact]
    public void Bearing_East_IsApproximately90()
    {
        // Great-circle bearing along a parallel isn't exactly 90° (the rhumb
        // line would be, but we compute initial bearing). At mid-latitudes
        // we get ~89.5°.
        var b = GeoMath.BearingDeg(52.0, 0, 52.0, 1.0);
        Assert.InRange(b, 89, 91);
    }

    [Fact]
    public void Bearing_North_Is0()
    {
        Assert.Equal(0, GeoMath.BearingDeg(52.0, 0, 53.0, 0), 1);
    }

    [Fact]
    public void Bearing_South_Is180()
    {
        Assert.Equal(180, GeoMath.BearingDeg(52.0, 0, 51.0, 0), 1);
    }

    [Fact]
    public void Bearing_West_IsApproximately270()
    {
        var b = GeoMath.BearingDeg(52.0, 0, 52.0, -1.0);
        Assert.InRange(b, 269, 271);
    }
}
