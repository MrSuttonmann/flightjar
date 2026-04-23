namespace FlightJar.Core.State;

/// <summary>
/// Equirectangular-approximation geo helpers. Mirrors pyModeS 3.2.0
/// <c>_approx_distance_km</c> and <c>_bearing_deg</c>.
/// </summary>
public static class GeoMath
{
    public const double EarthKm = 6371.0;

    /// <summary>Equirectangular distance approximation — accurate enough for the
    /// sub-20-km corrections we care about here, and cheaper than haversine.</summary>
    public static double ApproxDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dlat = Deg2Rad(lat2 - lat1);
        var dlon = Deg2Rad(lon2 - lon1) * Math.Cos(Deg2Rad((lat1 + lat2) / 2));
        return Math.Sqrt(dlat * dlat + dlon * dlon) * EarthKm;
    }

    /// <summary>Initial bearing from (lat1, lon1) to (lat2, lon2) in degrees
    /// (0 = N, 90 = E). Accurate to within a degree or so — fine for
    /// plausibility checks.</summary>
    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = Deg2Rad(lat1);
        var phi2 = Deg2Rad(lat2);
        var dlon = Deg2Rad(lon2 - lon1);
        var y = Math.Sin(dlon) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dlon);
        var bearing = Rad2Deg(Math.Atan2(y, x));
        return (bearing + 360.0) % 360.0;
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
    private static double Rad2Deg(double r) => r * 180.0 / Math.PI;
}
