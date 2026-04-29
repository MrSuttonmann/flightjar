namespace FlightJar.Decoder.ModeS;

/// <summary>
/// Unified decoding result. Mirrors the dict pyModeS 3.2.0 <c>decode()</c>
/// returns: DF-specific fields populated, others null.
/// </summary>
public sealed record DecodedMessage
{
    public required int Df { get; init; }
    public string? Icao { get; init; }

    /// <summary>Only meaningful for DF17/18. Defaults to true for surveillance DFs.</summary>
    public bool CrcValid { get; init; } = true;

    /// <summary>DF18 control field (3 bits, message bits 5-7). Null for DF17
    /// and non-ADS-B DFs. CF 0/1 = ADS-B from non-transponder devices;
    /// CF 2 = mlat-client / fine TIS-B; CF 3 = coarse TIS-B; CF 6 = ADS-R
    /// rebroadcast; CF 4/5/7 = reserved / management. The state layer maps
    /// these to a <c>PositionSource</c> when a fix is accepted.</summary>
    public int? Cf { get; init; }

    public int? Typecode { get; init; }

    // Identification (TC 1-4)
    public string? Callsign { get; init; }
    public int? Category { get; init; }

    // Altitude — baro (DF4/20 + TC 5-18) or geo (TC 20-22); caller selects the
    // right aircraft-state field by inspecting Typecode.
    public int? Altitude { get; init; }

    // Position — raw CPR fields (pair / reference decode done separately).
    public int? CprFormat { get; init; }
    public int? CprLat { get; init; }
    public int? CprLon { get; init; }

    // Velocity (TC 19)
    public double? Groundspeed { get; init; }
    public double? Airspeed { get; init; }
    public double? Track { get; init; }
    public double? Heading { get; init; }
    public int? VerticalRate { get; init; }

    // Squawk (DF5/21)
    public string? Squawk { get; init; }

    // Comm-B decoded fields (DF20/21). Only populated when the 56-bit MB
    // payload unambiguously matches exactly one heuristic BDS register
    // (4,0 / 4,4 / 5,0 / 6,0) — see CommB.Infer.
    public string? Bds { get; init; }

    // BDS 4,0 — selected vertical intention + barometric setting.
    public int? SelectedAltitudeMcpFt { get; init; }
    public int? SelectedAltitudeFmsFt { get; init; }
    public double? QnhHpa { get; init; }

    // BDS 4,4 — meteorological routine air report.
    public int? FigureOfMerit { get; init; }
    public int? WindSpeedKt { get; init; }
    public double? WindDirectionDeg { get; init; }
    public double? StaticAirTemperatureC { get; init; }
    public int? StaticPressureHpa { get; init; }
    public int? Turbulence { get; init; }
    public double? HumidityPct { get; init; }

    // BDS 5,0 — track and turn.
    public double? RollDeg { get; init; }
    public double? TrueTrackDeg { get; init; }
    public int? GroundspeedKt { get; init; }
    public double? TrackRateDegPerS { get; init; }
    public int? TrueAirspeedKt { get; init; }

    // BDS 6,0 — heading and speed.
    public double? MagneticHeadingDeg { get; init; }
    public int? IndicatedAirspeedKt { get; init; }
    public double? Mach { get; init; }
    public int? BaroVerticalRateFpm { get; init; }
    public int? InertialVerticalRateFpm { get; init; }
}
