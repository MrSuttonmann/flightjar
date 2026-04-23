using FlightJar.Core.State;

namespace FlightJar.Core.Tests.State;

public class RoutePlausibilityTests
{
    private static readonly AirportInfo Bru = new(50.90, 4.48);
    private static readonly AirportInfo Fra = new(50.03, 8.57);
    private static readonly AirportInfo Lhr = new(51.47, -0.45);
    private static readonly AirportInfo Jfk = new(40.64, -73.78);

    [Fact]
    public void Accepts_MidflightOnCourse()
    {
        // Halfway between BRU and FRA, heading east toward FRA.
        Assert.True(RoutePlausibility.IsPlausible(
            acLat: 50.5, acLon: 6.5, acTrack: 110, onGround: false,
            origin: Bru, destination: Fra));
    }

    [Fact]
    public void Rejects_BearingPointingAway()
    {
        Assert.False(RoutePlausibility.IsPlausible(
            acLat: 51.0, acLon: 4.0, acTrack: 280, onGround: false,
            origin: Bru, destination: Fra));
    }

    [Fact]
    public void Rejects_AircraftWayOffCorridor()
    {
        Assert.False(RoutePlausibility.IsPlausible(
            acLat: 64.0, acLon: -22.0, acTrack: 90, onGround: false,
            origin: Bru, destination: Fra));
    }

    [Fact]
    public void Accepts_ApproachAnyHeading()
    {
        Assert.True(RoutePlausibility.IsPlausible(
            acLat: 50.10, acLon: 8.47, acTrack: 220, onGround: false,
            origin: Bru, destination: Fra));
    }

    [Fact]
    public void Accepts_TakeoffFromOrigin()
    {
        Assert.True(RoutePlausibility.IsPlausible(
            acLat: 51.10, acLon: 4.50, acTrack: 140, onGround: false,
            origin: Bru, destination: Fra));
    }

    [Fact]
    public void Permissive_WhenInputsMissing()
    {
        Assert.True(RoutePlausibility.IsPlausible(
            acLat: 50, acLon: 4, acTrack: 90, onGround: false,
            origin: null, destination: Fra));

        Assert.True(RoutePlausibility.IsPlausible(
            acLat: 50, acLon: 4, acTrack: 90, onGround: false,
            origin: Bru, destination: null));

        // No fix yet (fresh contact) — don't prejudge.
        Assert.True(RoutePlausibility.IsPlausible(
            acLat: null, acLon: null, acTrack: 90, onGround: false,
            origin: Bru, destination: Fra));
    }

    [Fact]
    public void IgnoresTrack_WhenOnGround()
    {
        Assert.True(RoutePlausibility.IsPlausible(
            acLat: 50.90, acLon: 4.48, acTrack: 200, onGround: true,
            origin: Bru, destination: Fra));
    }

    [Fact]
    public void LongHaul_MidcourseOverAtlantic()
    {
        // LHR→JFK halfway across the Atlantic, tracking roughly west.
        Assert.True(RoutePlausibility.IsPlausible(
            acLat: 51.0, acLon: -35.0, acTrack: 280, onGround: false,
            origin: Lhr, destination: Jfk));
    }
}
