namespace FlightJar.Core.State;

/// <summary>
/// Per-aircraft state accumulated from Mode S messages. Owned by
/// <see cref="AircraftRegistry"/>; mutated on the ingest thread, snapshotted
/// atomically into immutable output records for readers.
/// </summary>
public sealed class Aircraft
{
    public required string Icao { get; init; }

    public string? Callsign { get; set; }
    public int? Category { get; set; }

    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public int? AltitudeBaro { get; set; }
    public int? AltitudeGeo { get; set; }
    public double? Track { get; set; }
    public double? Speed { get; set; }
    public int? Vrate { get; set; }
    public string? Squawk { get; set; }
    public bool OnGround { get; set; }

    public double LastSeen { get; set; }
    public double LastPositionTime { get; set; }
    public double FirstSeen { get; set; }
    public long? LastSeenMlat { get; set; }

    // CPR pair state for global airborne decode. We store the raw 17-bit
    // lat/lon fields (already extracted by the decoder) rather than the
    // full hex message — avoids re-decoding on pair decode.
    public int? EvenCprLat { get; set; }
    public int? EvenCprLon { get; set; }
    public double EvenT { get; set; }
    public int? OddCprLat { get; set; }
    public int? OddCprLon { get; set; }
    public double OddT { get; set; }

    /// <summary>
    /// Recent position history, bounded to
    /// <see cref="AircraftRegistry.TrailMaxPoints"/> (~5 minutes at 1 Hz).
    /// </summary>
    public List<TrailPoint> Trail { get; } = new();

    /// <summary>
    /// Monotonic revision counter bumped on any trail mutation. Lets the
    /// snapshot builder cache the serialised trail list and skip rebuilding
    /// it every tick when the aircraft hasn't moved.
    /// </summary>
    public int TrailRevision { get; set; }

    internal int SnapshotTrailRevision { get; set; } = -1;
    internal IReadOnlyList<object?[]>? SnapshotTrail { get; set; }

    public long MsgCount { get; set; }

    /// <summary>Peak BEAST signal byte (0-255) seen for this aircraft.</summary>
    public byte? SignalPeak { get; set; }

    /// <summary>Best-known altitude: prefer barometric, fall back to GNSS.</summary>
    public int? Altitude => AltitudeBaro ?? AltitudeGeo;

    // Comm-B (DF 20/21) derived state. Only populated when an EHS
    // interrogator near the receiver triggers the relevant BDS register,
    // so these fields are typically sparse and go stale quickly. The
    // `*At` timestamps record when each field was last updated so the
    // snapshot builder can expire them independently.

    // BDS 4,0 — selected vertical intention + baro setting.
    public int? SelectedAltitudeMcpFt { get; set; }
    public int? SelectedAltitudeFmsFt { get; set; }
    public double? QnhHpa { get; set; }
    public double Bds40At { get; set; }

    // BDS 4,4 — meteorological routine air report.
    public int? WindSpeedKt { get; set; }
    public double? WindDirectionDeg { get; set; }
    public double? StaticAirTemperatureC { get; set; }
    public int? StaticPressureHpa { get; set; }
    public int? Turbulence { get; set; }
    public double? HumidityPct { get; set; }
    public double Bds44At { get; set; }

    // BDS 5,0 — track and turn.
    public double? RollDeg { get; set; }
    public double? TrueTrackDeg { get; set; }
    public int? GroundspeedKt { get; set; }
    public double? TrackRateDegPerS { get; set; }
    public int? TrueAirspeedKt { get; set; }
    public double Bds50At { get; set; }

    // BDS 6,0 — heading and speed.
    public double? MagneticHeadingDeg { get; set; }
    public int? IndicatedAirspeedKt { get; set; }
    public double? Mach { get; set; }
    public int? BaroVerticalRateFpm { get; set; }
    public int? InertialVerticalRateFpm { get; set; }
    public double Bds60At { get; set; }
}
