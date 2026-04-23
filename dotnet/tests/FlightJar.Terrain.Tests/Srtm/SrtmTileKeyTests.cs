using FlightJar.Terrain.Srtm;

namespace FlightJar.Terrain.Tests.Srtm;

public class SrtmTileKeyTests
{
    [Theory]
    [InlineData(52, -2, "N52W002")]
    [InlineData(-33, 151, "S33E151")]
    [InlineData(0, 0, "N00E000")]
    [InlineData(-1, -1, "S01W001")]
    [InlineData(89, 179, "N89E179")]
    public void Name_matches_skadi_format(int lat, int lon, string expected)
    {
        Assert.Equal(expected, new SrtmTileKey(lat, lon).Name);
    }

    [Theory]
    [InlineData(52.7, -1.3, 52, -2)]
    [InlineData(52.0, -2.0, 52, -2)]
    [InlineData(-33.5, 151.2, -34, 151)]
    [InlineData(0.1, 0.1, 0, 0)]
    [InlineData(-0.1, -0.1, -1, -1)]
    public void Containing_returns_tile_by_sw_corner(double lat, double lon, int south, int west)
    {
        Assert.Equal(new SrtmTileKey(south, west), SrtmTileKey.Containing(lat, lon));
    }
}
