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
}
