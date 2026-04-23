namespace FlightJar.Core.State;

/// <summary>
/// Classifies a snapshot aircraft's phase of flight. Mirrors pyModeS 3.2.0
/// <c>flight_phase</c> in <c>app/aircraft.py</c>.
/// </summary>
public static class FlightPhase
{
    public const int ClimbVrate = 500;
    public const int CruiseAlt = 10000;
    public const double ApproachDistKm = 50.0;

    /// <summary>
    /// Returns one of "taxi" / "climb" / "descent" / "cruise" / "approach",
    /// or null when there isn't enough signal to decide.
    /// </summary>
    public static string? Classify(
        bool onGround,
        int? altitude,
        int? verticalRate,
        double? lat,
        double? lon,
        AirportInfo? destination)
    {
        if (onGround)
        {
            return "taxi";
        }

        if (altitude is int alt && alt < CruiseAlt
            && destination is AirportInfo dest
            && lat is double acLat && lon is double acLon)
        {
            var dist = GeoMath.ApproxDistanceKm(acLat, acLon, dest.Lat, dest.Lon);
            if (dist < ApproachDistKm)
            {
                return "approach";
            }
        }

        if (verticalRate is int vr)
        {
            if (vr > ClimbVrate)
            {
                return "climb";
            }
            if (vr < -ClimbVrate)
            {
                return "descent";
            }
        }

        if (altitude is int a && a > CruiseAlt)
        {
            return "cruise";
        }

        return null;
    }
}
