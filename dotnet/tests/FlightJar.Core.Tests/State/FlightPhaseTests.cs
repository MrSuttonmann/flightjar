using FlightJar.Core.State;

namespace FlightJar.Core.Tests.State;

public class FlightPhaseTests
{
    [Fact]
    public void Taxi_WinsOverEverything()
    {
        var phase = FlightPhase.Classify(
            onGround: true, altitude: 0, verticalRate: 1000,
            lat: null, lon: null, destination: null);
        Assert.Equal("taxi", phase);
    }

    [Fact]
    public void ClassifiesClimbAndDescentFromVrate()
    {
        Assert.Equal("climb", FlightPhase.Classify(
            onGround: false, altitude: 5000, verticalRate: 1500,
            lat: null, lon: null, destination: null));

        Assert.Equal("descent", FlightPhase.Classify(
            onGround: false, altitude: 5000, verticalRate: -1500,
            lat: null, lon: null, destination: null));
    }

    [Fact]
    public void Cruise_Above10000WithLevelFlight()
    {
        Assert.Equal("cruise", FlightPhase.Classify(
            onGround: false, altitude: 35000, verticalRate: 0,
            lat: null, lon: null, destination: null));

        // Low vrate still reads as cruise above the cruise-alt floor.
        Assert.Equal("cruise", FlightPhase.Classify(
            onGround: false, altitude: 35000, verticalRate: 200,
            lat: null, lon: null, destination: null));
    }

    [Fact]
    public void Approach_WinsOverClimbNearDestination()
    {
        // Plane low + close to a known destination reads as Approach even
        // while briefly climbing (go-around).
        var phase = FlightPhase.Classify(
            onGround: false, altitude: 3000, verticalRate: 800,
            lat: 51.48, lon: -0.45,
            destination: new AirportInfo(51.4700, -0.4543));
        Assert.Equal("approach", phase);
    }

    [Fact]
    public void Approach_NeedsDestWithin50Km()
    {
        var phase = FlightPhase.Classify(
            onGround: false, altitude: 3000, verticalRate: -600,
            lat: 52.0, lon: -1.0,
            destination: new AirportInfo(40.64, -73.78));
        Assert.Equal("descent", phase);
    }

    [Fact]
    public void ReturnsNull_WhenIndeterminate()
    {
        Assert.Null(FlightPhase.Classify(
            onGround: false, altitude: null, verticalRate: null,
            lat: null, lon: null, destination: null));

        Assert.Null(FlightPhase.Classify(
            onGround: false, altitude: 5000, verticalRate: null,
            lat: null, lon: null, destination: null));
    }
}
