namespace FlightJar.Core.State;

/// <summary>
/// Great-circle position projection for dead-reckoning stale aircraft
/// positions. Mirrors pyModeS 3.2.0 <c>_dead_reckon</c>.
/// </summary>
public static class DeadReckoning
{
    /// <summary>
    /// Project a position along a great-circle track at groundspeed.
    /// All inputs in canonical wire units: lat/lon degrees, track degrees
    /// (0=N, 90=E), groundspeed knots, elapsed seconds.
    /// </summary>
    public static (double Lat, double Lon) Project(
        double lat, double lon, double trackDeg, double speedKn, double elapsedSec)
    {
        var distKm = (speedKn * 1.852) * (elapsedSec / 3600.0);
        if (distKm <= 0)
        {
            return (lat, lon);
        }
        var phi1 = Deg2Rad(lat);
        var lam1 = Deg2Rad(lon);
        var theta = Deg2Rad(trackDeg);
        var d = distKm / GeoMath.EarthKm;
        var phi2 = Math.Asin(Math.Sin(phi1) * Math.Cos(d)
                           + Math.Cos(phi1) * Math.Sin(d) * Math.Cos(theta));
        var lam2 = lam1 + Math.Atan2(
            Math.Sin(theta) * Math.Sin(d) * Math.Cos(phi1),
            Math.Cos(d) - Math.Sin(phi1) * Math.Sin(phi2));
        var newLat = Rad2Deg(phi2);
        var newLon = ((Rad2Deg(lam2) + 540.0) % 360.0) - 180.0;
        return (newLat, newLon);
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
    private static double Rad2Deg(double r) => r * 180.0 / Math.PI;
}
