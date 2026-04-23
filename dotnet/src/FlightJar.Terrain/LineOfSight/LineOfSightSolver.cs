namespace FlightJar.Terrain.LineOfSight;

/// <summary>Receiver geometry for an LOS query.</summary>
/// <param name="Lat">Receiver latitude (deg).</param>
/// <param name="Lon">Receiver longitude (deg).</param>
/// <param name="AntennaMslM">Antenna-tip altitude in metres MSL (absolute, not AGL).</param>
public readonly record struct LosReceiver(double Lat, double Lon, double AntennaMslM);

/// <summary>Target point for an LOS query.</summary>
/// <param name="Lat">Target latitude (deg).</param>
/// <param name="Lon">Target longitude (deg).</param>
/// <param name="AltitudeMslM">Target altitude in metres MSL (typical cruise flight-level in metres).</param>
public readonly record struct LosTarget(double Lat, double Lon, double AltitudeMslM);

/// <summary>
/// Result of a line-of-sight solve.
///
/// <para>
/// <see cref="Blocked"/> is the verdict at the receiver's current antenna
/// MSL height. <see cref="RequiredAntennaMslM"/> is the minimum antenna tip
/// altitude (MSL) that would clear the obstruction, if one exists within
/// the <c>ceilingMslM</c> searched; otherwise null (unreachable).
/// </para>
/// </summary>
public readonly record struct LosResult(bool Blocked, double? RequiredAntennaMslM);

/// <summary>
/// Radio line-of-sight solver with a 4/3-Earth refraction model. Walks the
/// great-circle path between receiver and target, sampling terrain every
/// <c>pathStepM</c> metres, and checks whether the straight line from
/// antenna-tip to target stays above the refraction-corrected terrain profile.
/// </summary>
/// <remarks>
/// Standard VHF/UHF radio-horizon approximation: the real atmosphere refracts
/// the ray slightly downward, which is modelled by pretending the Earth has
/// radius 4/3 of its true value. At receiver range <c>d</c> the apparent
/// terrain is dropped by <c>d² / (2·R_eff)</c> below its true height, which is
/// why antennas "see" a bit further than the geometric horizon would suggest.
/// </remarks>
public static class LineOfSightSolver
{
    /// <summary>Evaluate LOS at the receiver's configured antenna altitude.</summary>
    /// <param name="ceilingMslM">
    /// Upper bound for the bisection — the antenna won't be considered above
    /// this absolute MSL height. Typical pattern: caller computes
    /// <c>ground_elev + max_agl</c> so the search corresponds to "how much
    /// could I physically raise my antenna above local ground".
    /// </param>
    public static LosResult Solve(
        LosReceiver receiver,
        LosTarget target,
        ITerrainSampler sampler,
        double pathStepM = 100.0,
        double ceilingMslM = double.PositiveInfinity,
        double bisectionToleranceM = 1.0)
    {
        var distanceM = GreatCircle.DistanceMetres(receiver.Lat, receiver.Lon, target.Lat, target.Lon);
        if (distanceM <= 0)
        {
            return new LosResult(false, receiver.AntennaMslM);
        }
        var bearingDeg = GreatCircle.InitialBearingDeg(receiver.Lat, receiver.Lon, target.Lat, target.Lon);

        // Sample the terrain profile once — the same profile is reused across
        // the bisection iterations.
        var profile = SampleProfile(receiver.Lat, receiver.Lon, bearingDeg, distanceM, pathStepM, sampler);

        var targetEff = target.AltitudeMslM - (distanceM * distanceM) / (2.0 * GreatCircle.EffectiveRadiusMetres);

        bool blockedNow = IsBlocked(profile, distanceM, receiver.AntennaMslM, targetEff);
        if (!blockedNow)
        {
            return new LosResult(false, null);
        }

        // Bisect on antenna MSL height to find the minimum that clears. A cell
        // that's still blocked at the ceiling is marked "unreachable"
        // (RequiredAntennaMslM == null).
        var lo = receiver.AntennaMslM;
        var hi = ceilingMslM;
        if (hi <= lo || IsBlocked(profile, distanceM, hi, targetEff))
        {
            return new LosResult(true, null);
        }
        while ((hi - lo) > bisectionToleranceM)
        {
            var mid = (lo + hi) / 2.0;
            if (IsBlocked(profile, distanceM, mid, targetEff))
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return new LosResult(true, hi);
    }

    /// <summary>
    /// Walk the great-circle path from (startLat, startLon) forward
    /// <paramref name="distanceM"/> along <paramref name="bearingDeg"/>, sampling
    /// the effective (refraction-corrected) terrain at intervals of
    /// <paramref name="stepM"/>. Endpoints are included. Profile[i].D is the
    /// arc distance from the receiver; Profile[i].Elev is the real terrain
    /// elevation *after* the 4/3-Earth bulge has been subtracted (i.e. the
    /// value we compare the straight LOS line against).
    /// </summary>
    private static (double D, double Elev)[] SampleProfile(
        double startLat, double startLon, double bearingDeg, double distanceM,
        double stepM, ITerrainSampler sampler)
    {
        var n = Math.Max(2, (int)Math.Ceiling(distanceM / stepM) + 1);
        var profile = new (double D, double Elev)[n];
        for (var i = 0; i < n; i++)
        {
            var d = distanceM * i / (n - 1);
            var (lat, lon) = GreatCircle.Destination(startLat, startLon, bearingDeg, d);
            var elev = sampler.ElevationMetres(lat, lon);
            var bulge = (d * d) / (2.0 * GreatCircle.EffectiveRadiusMetres);
            profile[i] = (d, elev - bulge);
        }
        return profile;
    }

    /// <summary>
    /// Straight LOS check: given antenna-tip elevation <paramref name="rxElev"/>
    /// at d=0 and target-effective elevation <paramref name="targetEff"/> at
    /// d=<paramref name="distanceM"/>, return true if any intermediate sample of
    /// the terrain profile rises above the line.
    /// </summary>
    private static bool IsBlocked(
        (double D, double Elev)[] profile, double distanceM, double rxElev, double targetEff)
    {
        // Skip the first and last samples — they coincide with the receiver and
        // target endpoints, which are the defining points of the line itself.
        for (var i = 1; i < profile.Length - 1; i++)
        {
            var (d, elev) = profile[i];
            var lineY = rxElev + (targetEff - rxElev) * (d / distanceM);
            if (elev > lineY)
            {
                return true;
            }
        }
        return false;
    }
}
