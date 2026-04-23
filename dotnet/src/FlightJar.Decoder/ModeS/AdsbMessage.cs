namespace FlightJar.Decoder.ModeS;

/// <summary>
/// Decoded DF17/18 (ADS-B) fields. Populated fields depend on the typecode.
/// </summary>
public sealed record AdsbMessage
{
    public required int Typecode { get; init; }

    /// <summary>TC 1-4: aircraft category (0-7).</summary>
    public int? Category { get; init; }

    /// <summary>TC 1-4: decoded callsign (trimmed).</summary>
    public string? Callsign { get; init; }

    /// <summary>TC 5-8 (surface), TC 9-18 (baro), TC 20-22 (GNSS): altitude feet.</summary>
    public int? Altitude { get; init; }

    /// <summary>TC 5-8: true when the aircraft is on the ground.</summary>
    public bool OnGround { get; init; }

    /// <summary>TC 5-8, 9-18, 20-22: CPR format (0 = even, 1 = odd).</summary>
    public int? CprFormat { get; init; }

    /// <summary>TC 5-8, 9-18, 20-22: raw 17-bit CPR latitude.</summary>
    public int? CprLat { get; init; }

    /// <summary>TC 5-8, 9-18, 20-22: raw 17-bit CPR longitude.</summary>
    public int? CprLon { get; init; }

    /// <summary>TC 19 subtypes 1/2; TC 5-8 (surface): ground speed in knots.</summary>
    public double? Groundspeed { get; init; }

    /// <summary>TC 19 subtypes 1/2; TC 5-8: track in degrees.</summary>
    public double? Track { get; init; }

    /// <summary>TC 19 subtypes 3/4: airspeed in knots.</summary>
    public double? Airspeed { get; init; }

    /// <summary>TC 19 subtypes 3/4: magnetic heading in degrees.</summary>
    public double? Heading { get; init; }

    /// <summary>TC 19: vertical rate in ft/min (sign = climb/descent).</summary>
    public int? VerticalRate { get; init; }

    /// <summary>TC 19: "BARO" or "GNSS" source for the vertical rate.</summary>
    public string? VerticalRateSource { get; init; }
}
