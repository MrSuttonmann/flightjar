using FlightJar.Core.State;

namespace FlightJar.Core.Tests.State;

public class DeadReckoningTests
{
    [Fact]
    public void Project_DueEast10Seconds_MovesLongitudeSlightly()
    {
        // 480 kn due east for 10 s -> ~2.47 km.
        var (lat, lon) = DeadReckoning.Project(52.0, -1.0, trackDeg: 90, speedKn: 480, elapsedSec: 10);
        Assert.Equal(52.0, lat, 4);
        Assert.InRange(lon - (-1.0), 0.02, 0.05);
    }

    [Fact]
    public void Project_ZeroElapsed_ReturnsSamePosition()
    {
        var (lat, lon) = DeadReckoning.Project(52.0, -1.0, trackDeg: 90, speedKn: 480, elapsedSec: 0);
        Assert.Equal(52.0, lat);
        Assert.Equal(-1.0, lon);
    }

    [Fact]
    public void Project_ZeroSpeed_ReturnsSamePosition()
    {
        var (lat, lon) = DeadReckoning.Project(52.0, -1.0, trackDeg: 90, speedKn: 0, elapsedSec: 10);
        Assert.Equal(52.0, lat);
        Assert.Equal(-1.0, lon);
    }
}
