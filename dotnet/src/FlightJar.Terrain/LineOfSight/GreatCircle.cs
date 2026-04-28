namespace FlightJar.Terrain.LineOfSight;

/// <summary>
/// Small spherical-geometry helpers used by <see cref="LineOfSightSolver"/>.
/// Receiver-to-target distances are short (&lt; a few hundred km) so a spherical
/// model (not WGS-84 ellipsoid) is more than adequate — the residual error is
/// well below the SRTM1 pixel (~30 m).
/// </summary>
public static class GreatCircle
{
    public const double EarthRadiusMetres = 6_371_008.8;

    /// <summary>Refractive ("4/3 earth") radius used for radio line-of-sight.</summary>
    public const double EffectiveRadiusMetres = 4.0 / 3.0 * EarthRadiusMetres;

    /// <summary>
    /// Radio horizon distance in metres for an antenna and a target separated by the
    /// 4/3-Earth bulge. Both heights are AGL relative to a flat-mean-elevation plane —
    /// the antenna's slice (sqrt(2·R_eff·h_a)) plus the target's slice
    /// (sqrt(2·R_eff·h_t)) is how far each side can see the shared horizon. Negative
    /// inputs are treated as zero (a sub-ground antenna or target sees no farther
    /// than ground level itself).
    /// </summary>
    public static double RadioHorizonDistanceMetres(double antennaHeightAglM, double targetHeightAglM)
    {
        var ha = Math.Max(0.0, antennaHeightAglM);
        var ht = Math.Max(0.0, targetHeightAglM);
        return Math.Sqrt(2.0 * EffectiveRadiusMetres * ha)
             + Math.Sqrt(2.0 * EffectiveRadiusMetres * ht);
    }

    /// <summary>Great-circle arc distance in metres between two points.</summary>
    public static double DistanceMetres(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = Deg2Rad(lat1);
        var phi2 = Deg2Rad(lat2);
        var dphi = Deg2Rad(lat2 - lat1);
        var dlam = Deg2Rad(lon2 - lon1);
        var a = Math.Sin(dphi / 2) * Math.Sin(dphi / 2)
              + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dlam / 2) * Math.Sin(dlam / 2);
        return 2 * EarthRadiusMetres * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// <summary>Initial true bearing (degrees 0..360) from point 1 to point 2.</summary>
    public static double InitialBearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = Deg2Rad(lat1);
        var phi2 = Deg2Rad(lat2);
        var dlam = Deg2Rad(lon2 - lon1);
        var y = Math.Sin(dlam) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2)
              - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dlam);
        var bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
        return (bearing + 360.0) % 360.0;
    }

    /// <summary>
    /// Walk <paramref name="distanceMetres"/> along the great-circle initial bearing
    /// <paramref name="bearingDeg"/> from (lat, lon). Used to place sample points
    /// along a line-of-sight path.
    /// </summary>
    public static (double Lat, double Lon) Destination(
        double lat, double lon, double bearingDeg, double distanceMetres)
    {
        var phi1 = Deg2Rad(lat);
        var lam1 = Deg2Rad(lon);
        var theta = Deg2Rad(bearingDeg);
        var d = distanceMetres / EarthRadiusMetres;

        var phi2 = Math.Asin(
            Math.Sin(phi1) * Math.Cos(d)
          + Math.Cos(phi1) * Math.Sin(d) * Math.Cos(theta));
        var lam2 = lam1 + Math.Atan2(
            Math.Sin(theta) * Math.Sin(d) * Math.Cos(phi1),
            Math.Cos(d) - Math.Sin(phi1) * Math.Sin(phi2));
        return (phi2 * 180.0 / Math.PI, ((lam2 * 180.0 / Math.PI) + 540.0) % 360.0 - 180.0);
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
}
