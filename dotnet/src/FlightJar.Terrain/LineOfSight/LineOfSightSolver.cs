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
/// One terrain sample along the LOS path that the straight line failed to
/// clear at the receiver's current antenna height. <see cref="ElevMslM"/> is
/// the *true* terrain elevation (refraction bulge added back), so the value
/// reads like a topographic map height rather than a corrected internal
/// quantity.
/// </summary>
public readonly record struct LosObstruction(double Lat, double Lon, double ElevMslM);

/// <summary>
/// Result of a line-of-sight solve.
///
/// <para>
/// <see cref="Blocked"/> is the verdict at the receiver's current antenna
/// MSL height. <see cref="RequiredAntennaMslM"/> is the minimum antenna tip
/// altitude (MSL) that would clear the obstruction, if one exists within
/// the <c>ceilingMslM</c> searched; otherwise null (unreachable).
/// <see cref="Obstruction"/> is the worst-offending terrain sample at the
/// configured antenna height — the one hill / ridge actually causing the
/// blockage — populated only when <see cref="Blocked"/> is true.
/// </para>
/// </summary>
public readonly record struct LosResult(
    bool Blocked, double? RequiredAntennaMslM, LosObstruction? Obstruction);

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
            return new LosResult(false, receiver.AntennaMslM, null);
        }
        var bearingDeg = GreatCircle.InitialBearingDeg(receiver.Lat, receiver.Lon, target.Lat, target.Lon);

        // Sample the terrain profile once — the same profile is reused across
        // the bisection iterations.
        var profile = SampleProfile(receiver.Lat, receiver.Lon, bearingDeg, distanceM, pathStepM, sampler);

        var targetEff = target.AltitudeMslM - (distanceM * distanceM) / (2.0 * GreatCircle.EffectiveRadiusMetres);

        bool blockedNow = IsBlocked(profile, distanceM, receiver.AntennaMslM, targetEff);
        if (!blockedNow)
        {
            return new LosResult(false, null, null);
        }

        // Identify the single worst-offending sample at the user's *current*
        // antenna height — the one hill or ridge whose elevation rises most
        // above the LOS line. That's the actionable answer to "what's blocking
        // me right now"; aggregating it across cells turns into the blocker
        // overlay. Searched once (not per bisection iteration) since raising
        // the antenna mid-bisection doesn't change which sample is worst, only
        // by how much it offends.
        var obstruction = FindWorstObstruction(
            profile, distanceM, receiver.AntennaMslM, targetEff,
            receiver.Lat, receiver.Lon, bearingDeg);

        // Bisect on antenna MSL height to find the minimum that clears. A cell
        // that's still blocked at the ceiling is marked "unreachable"
        // (RequiredAntennaMslM == null).
        var lo = receiver.AntennaMslM;
        var hi = ceilingMslM;
        if (hi <= lo || IsBlocked(profile, distanceM, hi, targetEff))
        {
            return new LosResult(true, null, obstruction);
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
        return new LosResult(true, hi, obstruction);
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

    /// <summary>
    /// Pick the sample defining the silhouette ridge from the receiver's
    /// perspective: the one with the maximum angular elevation
    /// <c>(elev - rxElev) / d</c>. By construction nothing closer to the
    /// receiver is taller in angular terms, so the picked sample isn't
    /// hidden behind a nearer hill — a far peak that's geometrically above
    /// the LOS line but obscured by a nearer ridge would have lower angular
    /// elevation and lose to the ridge. Samples whose true terrain MSL is at
    /// or below sea level are skipped: SRTM ocean tiles and no-data voids
    /// both report 0 m, and they aren't real obstacles even when earth
    /// curvature mathematically puts them above a long, low-target LOS line.
    /// Returns null when no intermediate sample qualifies — the cell stays
    /// blocked (per <see cref="IsBlocked"/>) but the blocker overlay simply
    /// has no real-terrain obstruction to attribute the shadow to. The
    /// emitted elevation is true terrain MSL (refraction bulge added back)
    /// so it reads as a topographic height rather than an internal
    /// corrected value.
    /// </summary>
    private static LosObstruction? FindWorstObstruction(
        (double D, double Elev)[] profile, double distanceM, double rxElev, double targetEff,
        double rxLat, double rxLon, double bearingDeg)
    {
        var bestAngular = double.NegativeInfinity;
        var bestIdx = -1;
        for (var i = 1; i < profile.Length - 1; i++)
        {
            var (d, elev) = profile[i];
            var trueMsl = elev + (d * d) / (2.0 * GreatCircle.EffectiveRadiusMetres);
            if (trueMsl <= 0)
            {
                continue;
            }
            var angular = (elev - rxElev) / d;
            if (angular > bestAngular)
            {
                bestAngular = angular;
                bestIdx = i;
            }
        }
        if (bestIdx < 0) return null;
        // The silhouette ridge among real terrain might not itself break the
        // line to the target — IsBlocked may have triggered on a sea-level
        // sample we just filtered out. Only report when the ridge actually
        // exceeds the LOS slope to the target.
        var lineSlope = (targetEff - rxElev) / distanceM;
        if (bestAngular <= lineSlope) return null;
        var sample = profile[bestIdx];
        var (lat, lon) = GreatCircle.Destination(rxLat, rxLon, bearingDeg, sample.D);
        var trueMslOut = sample.Elev + (sample.D * sample.D) / (2.0 * GreatCircle.EffectiveRadiusMetres);
        return new LosObstruction(lat, lon, trueMslOut);
    }
}
