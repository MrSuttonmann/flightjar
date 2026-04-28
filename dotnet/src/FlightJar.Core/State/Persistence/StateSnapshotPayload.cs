namespace FlightJar.Core.State.Persistence;

/// <summary>Full serialised <c>AircraftRegistry</c> state — the payload
/// round-tripped through <c>FlightJar.Persistence.State.StateSnapshotStore</c>.
/// Lives in Core so the registry can reference it without inverting the
/// project graph.</summary>
public sealed class StateSnapshotPayload
{
    public int Version { get; set; } = 1;
    public double SavedAt { get; set; }
    public Dictionary<string, PersistedAircraft> Aircraft { get; set; } = new();
}

public sealed class PersistedAircraft
{
    public string Icao { get; set; } = "";
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
    public long MsgCount { get; set; }
    public byte? SignalPeak { get; set; }
    public List<PersistedTrailPoint> Trail { get; set; } = new();
}

public sealed record PersistedTrailPoint(
    double Lat, double Lon, int? Altitude, double? Speed, double Timestamp, bool Gap);
