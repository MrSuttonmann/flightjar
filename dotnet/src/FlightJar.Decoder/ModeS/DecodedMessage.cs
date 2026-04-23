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
}
