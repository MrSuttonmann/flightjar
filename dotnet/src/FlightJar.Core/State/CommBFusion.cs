namespace FlightJar.Core.State;

/// <summary>
/// Pure projection: roll up an <see cref="Aircraft"/>'s Comm-B (DF 20/21
/// Enhanced Mode S) accumulators into the snapshot record, dropping fields
/// older than <see cref="CommBMaxAge"/> and deriving SAT/TAT from BDS 5,0
/// + 6,0 when the aircraft never opted into BDS 4,4.
/// <para>
/// Temperature source priority:
/// </para>
/// <list type="number">
///   <item>If BDS 4,4 is fresh, use its direct SAT reading (ground-truth).</item>
///   <item>Otherwise, if BDS 5,0 (TAS) and BDS 6,0 (Mach) are both fresh,
///     derive SAT from the TAS/Mach relation: <c>a = TAS / M</c> is the
///     local speed of sound, and <c>T_K = a² / (γR)</c> with γ=1.4 and
///     R=287.05 J/(kg·K) (γR ≈ 401.874). BDS 4,4 is pilot-optional on
///     most airframes, so in practice most EHS-interrogated aircraft only
///     emit BDS 4,0 / 5,0 / 6,0 and the derivation is the only way to
///     surface OAT for them.</item>
/// </list>
/// <para>
/// TAT is always derived from whichever SAT source is active, via the
/// standard compressible-flow stagnation-temperature relation (recovery
/// factor 1.0): in Kelvin, <c>TAT = SAT * (1 + 0.2 * M²)</c>.
/// </para>
/// <para>
/// Returns null if no Comm-B field is fresh — the frontend uses a single
/// null check to suppress the panel.
/// </para>
/// </summary>
public static class CommBFusion
{
    /// <summary>
    /// How long a Comm-B field survives after its last decode. Real-world EHS
    /// cadence is typically one BDS reply every few seconds per register, but
    /// coverage drops out when the aircraft leaves the interrogator's beam.
    /// 120 s is long enough to ride through a couple of missed sweeps without
    /// blanking the panel, short enough to not display obviously stale values.
    /// </summary>
    public const double CommBMaxAge = 120.0;

    /// <summary>γR for dry air (γ=1.4, R=287.05 J/(kg·K)). T_K = a² / γR.</summary>
    private const double GammaR = 401.874;

    /// <summary>1 knot in m/s.</summary>
    private const double KnotsToMps = 0.5144444;

    /// <summary>
    /// Lower bound on derived SAT (Kelvin). Below this is almost certainly
    /// noise from a transient TAS/Mach inconsistency rather than real OAT.
    /// </summary>
    private const double DerivedSatMinK = 150.0;

    /// <summary>Upper bound on derived SAT (Kelvin); see <see cref="DerivedSatMinK"/>.</summary>
    private const double DerivedSatMaxK = 320.0;

    /// <summary>Mach floor below which the TAS/Mach derivation is too noisy to use.</summary>
    private const double DerivedMachMin = 0.1;

    public static SnapshotCommB? Build(Aircraft ac, double now)
    {
        var bds40Fresh = ac.Bds40At > 0 && now - ac.Bds40At <= CommBMaxAge;
        var bds44Fresh = ac.Bds44At > 0 && now - ac.Bds44At <= CommBMaxAge;
        var bds50Fresh = ac.Bds50At > 0 && now - ac.Bds50At <= CommBMaxAge;
        var bds60Fresh = ac.Bds60At > 0 && now - ac.Bds60At <= CommBMaxAge;

        if (!bds40Fresh && !bds44Fresh && !bds50Fresh && !bds60Fresh)
        {
            return null;
        }

        double? sat = bds44Fresh ? ac.StaticAirTemperatureC : null;
        string? satSource = sat is not null ? "observed" : null;

        if (sat is null && bds50Fresh && bds60Fresh
            && ac.TrueAirspeedKt is int tasKt && tasKt > 0
            && ac.Mach is double machObs && machObs > DerivedMachMin)
        {
            var tasMps = tasKt * KnotsToMps;
            var aMps = tasMps / machObs;
            var tK = aMps * aMps / GammaR;
            // Physical plausibility: reject outside [150, 320] K. Outside this
            // range the input pair is almost certainly noise from a rapid
            // maneuver where TAS and Mach are transiently inconsistent —
            // better to blank than to show a wildly wrong figure.
            if (tK >= DerivedSatMinK && tK <= DerivedSatMaxK)
            {
                sat = tK - 273.15;
                satSource = "derived";
            }
        }

        double? tat = null;
        if (sat is double satC && bds60Fresh && ac.Mach is double mach)
        {
            var satK = satC + 273.15;
            var tatK = satK * (1 + 0.2 * mach * mach);
            tat = tatK - 273.15;
        }

        return new SnapshotCommB
        {
            SelectedAltitudeMcpFt = bds40Fresh ? ac.SelectedAltitudeMcpFt : null,
            SelectedAltitudeFmsFt = bds40Fresh ? ac.SelectedAltitudeFmsFt : null,
            QnhHpa = bds40Fresh ? ac.QnhHpa : null,
            Bds40At = bds40Fresh ? ac.Bds40At : null,

            WindSpeedKt = bds44Fresh ? ac.WindSpeedKt : null,
            WindDirectionDeg = bds44Fresh ? ac.WindDirectionDeg : null,
            StaticAirTemperatureC = sat,
            StaticAirTemperatureSource = satSource,
            TotalAirTemperatureC = tat,
            StaticPressureHpa = bds44Fresh ? ac.StaticPressureHpa : null,
            Turbulence = bds44Fresh ? ac.Turbulence : null,
            HumidityPct = bds44Fresh ? ac.HumidityPct : null,
            Bds44At = bds44Fresh ? ac.Bds44At : null,

            RollDeg = bds50Fresh ? ac.RollDeg : null,
            TrueTrackDeg = bds50Fresh ? ac.TrueTrackDeg : null,
            GroundspeedKt = bds50Fresh ? ac.GroundspeedKt : null,
            TrackRateDegPerS = bds50Fresh ? ac.TrackRateDegPerS : null,
            TrueAirspeedKt = bds50Fresh ? ac.TrueAirspeedKt : null,
            Bds50At = bds50Fresh ? ac.Bds50At : null,

            MagneticHeadingDeg = bds60Fresh ? ac.MagneticHeadingDeg : null,
            IndicatedAirspeedKt = bds60Fresh ? ac.IndicatedAirspeedKt : null,
            Mach = bds60Fresh ? ac.Mach : null,
            BaroVerticalRateFpm = bds60Fresh ? ac.BaroVerticalRateFpm : null,
            InertialVerticalRateFpm = bds60Fresh ? ac.InertialVerticalRateFpm : null,
            Bds60At = bds60Fresh ? ac.Bds60At : null,
        };
    }
}
