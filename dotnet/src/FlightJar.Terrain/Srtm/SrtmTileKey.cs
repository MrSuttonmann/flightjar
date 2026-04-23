using System.Globalization;

namespace FlightJar.Terrain.Srtm;

/// <summary>
/// One SRTM1 tile covers a 1°×1° cell, identified by its SW corner (southLat,
/// westLon) in integer degrees. The skadi naming scheme uses N/S + E/W
/// prefixes: e.g. (52, -2) → <c>N52W002</c>, (-33, 151) → <c>S33E151</c>.
/// </summary>
public readonly record struct SrtmTileKey(int SouthLat, int WestLon)
{
    /// <summary>Skadi basename without extension (e.g. <c>N52W002</c>).</summary>
    public string Name
    {
        get
        {
            var latDir = SouthLat >= 0 ? 'N' : 'S';
            var lonDir = WestLon >= 0 ? 'E' : 'W';
            var latAbs = Math.Abs(SouthLat);
            var lonAbs = Math.Abs(WestLon);
            return string.Create(CultureInfo.InvariantCulture, $"{latDir}{latAbs:D2}{lonDir}{lonAbs:D3}");
        }
    }

    /// <summary>Return the tile key containing the given point.</summary>
    public static SrtmTileKey Containing(double lat, double lon)
    {
        return new SrtmTileKey((int)Math.Floor(lat), (int)Math.Floor(lon));
    }

    public override string ToString() => Name;
}
