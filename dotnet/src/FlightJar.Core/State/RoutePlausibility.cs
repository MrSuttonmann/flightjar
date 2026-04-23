namespace FlightJar.Core.State;

/// <summary>
/// Cross-checks an adsbdb-supplied route (callsign → origin/destination)
/// against the aircraft's real position and track. Mirrors pyModeS 3.2.0
/// <c>is_plausible_route</c> in <c>app/aircraft.py</c>.
/// </summary>
public static class RoutePlausibility
{
    public const double BearingMaxDeltaDeg = 135.0;
    public const double NearDestKm = 50.0;
    public const double CorridorMult = 2.0;
    public const double CorridorAbsKm = 300.0;

    /// <summary>
    /// Returns false when the route is almost certainly wrong (plane well
    /// outside the corridor, or pointing away from the destination).
    /// Returns true whenever there isn't enough signal to judge.
    /// </summary>
    public static bool IsPlausible(
        double? acLat, double? acLon, double? acTrack, bool onGround,
        AirportInfo? origin, AirportInfo? destination)
    {
        if (origin is not AirportInfo o || destination is not AirportInfo d)
        {
            return true;
        }
        if (acLat is not double lat || acLon is not double lon)
        {
            return true;
        }

        var total = GeoMath.ApproxDistanceKm(o.Lat, o.Lon, d.Lat, d.Lon);
        if (total <= 0)
        {
            return true;
        }
        var fromOrigin = GeoMath.ApproxDistanceKm(o.Lat, o.Lon, lat, lon);
        var toDest = GeoMath.ApproxDistanceKm(lat, lon, d.Lat, d.Lon);
        var maxSum = Math.Max(CorridorMult * total, total + CorridorAbsKm);
        if (fromOrigin + toDest > maxSum)
        {
            return false;
        }

        if (acTrack is double track && !onGround && toDest > NearDestKm)
        {
            var bearing = GeoMath.BearingDeg(lat, lon, d.Lat, d.Lon);
            var delta = Math.Abs(((track - bearing) + 180.0) % 360.0 - 180.0);
            if (delta > BearingMaxDeltaDeg)
            {
                return false;
            }
        }
        return true;
    }
}
