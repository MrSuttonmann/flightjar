using FlightJar.Decoder.ModeS;

namespace FlightJar.Decoder.Tests.ModeS;

public class CprTests
{
    [Theory]
    [InlineData(0.0, 59)]
    [InlineData(10.0, 59)]
    [InlineData(45.0, 42)]
    [InlineData(52.98, 36)]
    [InlineData(87.0, 2)]
    [InlineData(88.0, 1)]
    [InlineData(-45.0, 42)]
    [InlineData(-52.98, 36)]
    public void Nl_MatchesPyModeS(double lat, int expected)
    {
        Assert.Equal(expected, Cpr.Nl(lat));
    }

    [Fact]
    public void AirbornePair_ResolvesPosition()
    {
        // Oracle: airborne_position_pair(93000, 51372, 74158, 50194, even_is_newer=True)
        // => (52.2572021484375, 3.91937255859375)
        var pos = Cpr.AirbornePositionPair(93000, 51372, 74158, 50194, evenIsNewer: true);
        Assert.NotNull(pos);
        Assert.Equal(52.2572021484375, pos!.Value.Lat, 12);
        Assert.Equal(3.91937255859375, pos.Value.Lon, 12);
    }

    [Fact]
    public void AirborneWithRef_Even_MatchesPair()
    {
        // Local decode of the even frame with a reference close to the true
        // position should reproduce the pair decode.
        var (lat, lon) = Cpr.AirbornePositionWithRef(
            cprFormat: 0, cprLatRaw: 93000, cprLonRaw: 51372,
            latRef: 52.0, lonRef: 4.0);
        Assert.Equal(52.2572021484375, lat, 12);
        Assert.Equal(3.91937255859375, lon, 12);
    }

    [Fact]
    public void AirborneWithRef_Odd()
    {
        var (lat, lon) = Cpr.AirbornePositionWithRef(
            cprFormat: 1, cprLatRaw: 74158, cprLonRaw: 50194,
            latRef: 52.0, lonRef: 4.0);
        Assert.Equal(52.26578017412606, lat, 10);
        Assert.Equal(3.938912527901786, lon, 10);
    }

    [Fact]
    public void SurfaceWithRef_Odd()
    {
        var (lat, lon) = Cpr.SurfacePositionWithRef(
            cprFormat: 1, cprLatRaw: 39195, cprLonRaw: 110320,
            latRef: 52.0, lonRef: 4.0);
        Assert.Equal(52.32056051997815, lat, 10);
        Assert.Equal(4.735735212053572, lon, 10);
    }
}
