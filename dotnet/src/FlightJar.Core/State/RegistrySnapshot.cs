namespace FlightJar.Core.State;

/// <summary>A read-only registry snapshot suitable for broadcasting to WS
/// clients and HTTP consumers. Mirrors the shape of Python's
/// <c>registry.snapshot(now)</c>.</summary>
public sealed record RegistrySnapshot(
    double Now,
    int Count,
    int Positioned,
    ReceiverInfo? Receiver,
    string? SiteName,
    IReadOnlyList<SnapshotAircraft> Aircraft)
{
    /// <summary>
    /// Aggregated airport lookup — keyed by ICAO code. One entry per
    /// unique origin / destination referenced across the snapshot's
    /// aircraft. The frontend reads it for route progress + METAR
    /// tooltips so data stays consistent across tail entries.
    /// </summary>
    public IReadOnlyDictionary<string, SnapshotAirportRef>? Airports { get; init; }

    /// <summary>Total BEAST frames ingested since startup. Let the frontend
    /// compute the current frame rate from consecutive snapshots.</summary>
    public long? Frames { get; init; }

    /// <summary>P2P relay status surfaced to the frontend so the sidebar
    /// can show connection state and peer count. Null when federation is
    /// disabled (env kill switch <c>P2P_ENABLED=0</c>) and the service
    /// was never registered.</summary>
    public SnapshotP2PStatus? P2P { get; init; }

    public static RegistrySnapshot Empty { get; } = new(
        Now: 0, Count: 0, Positioned: 0,
        Receiver: null, SiteName: null,
        Aircraft: Array.Empty<SnapshotAircraft>());
}

/// <summary>P2P federation status block on the snapshot. The frontend
/// reads <c>enabled</c> to decide whether to show the status line at all,
/// then <c>connected</c> / <c>peers</c> for the value.</summary>
public sealed record SnapshotP2PStatus(bool Enabled, bool Connected, int Peers);

/// <summary>Per-ICAO airport entry surfaced in the snapshot's
/// <see cref="RegistrySnapshot.Airports"/> map. Trimmed to what the
/// frontend reads: name + coords (for route-progress geometry) plus
/// the most recent METAR if we have one cached.</summary>
public sealed record SnapshotAirportRef(
    string Name,
    double Lat,
    double Lon)
{
    public SnapshotMetar? Metar { get; init; }
}

/// <summary>Wire-format METAR surfaced in the snapshot's airport map.
/// Field names match what <c>format.js</c>:<c>formatMetar</c> reads
/// (<c>wind_dir</c>, <c>wind_kt</c>, <c>visibility</c>, <c>cover</c>,
/// <c>raw</c>). Visibility is pass-through because aviationweather.gov
/// returns a number (metres) on non-US stations and a string ("10+")
/// on US ones.</summary>
public sealed record SnapshotMetar
{
    public string? Raw { get; init; }
    public long? ObsTime { get; init; }
    /// <summary>Wind direction — number (degrees) or string ("VRB").</summary>
    public System.Text.Json.JsonElement? WindDir { get; init; }
    public double? WindKt { get; init; }
    public double? GustKt { get; init; }
    public System.Text.Json.JsonElement? Visibility { get; init; }
    public double? TempC { get; init; }
    public double? DewpointC { get; init; }
    public double? AltimeterHpa { get; init; }
    public string? Cover { get; init; }
}

public sealed record SnapshotAircraft
{
    public required string Icao { get; init; }
    public string? Callsign { get; init; }
    public int? Category { get; init; }

    // Enriched from the aircraft DB (optional; null when no DB attached).
    public string? Registration { get; init; }
    public string? TypeIcao { get; init; }
    public string? TypeLong { get; init; }

    public double? Lat { get; init; }
    public double? Lon { get; init; }
    public bool PositionStale { get; init; }

    /// <summary>Origin of the most recent accepted position fix —
    /// <c>"adsb"</c> (direct broadcast) or <c>"mlat"</c> (computed by ground
    /// stations and relayed). Null until any position has been seen.</summary>
    public PositionSource? PositionSource { get; init; }

    public int? Altitude { get; init; }
    public int? AltitudeBaro { get; init; }
    public int? AltitudeGeo { get; init; }
    public double? Track { get; init; }
    public double? Speed { get; init; }
    public int? Vrate { get; init; }

    public string? Squawk { get; init; }
    public string? Emergency { get; init; }

    public bool OnGround { get; init; }

    public double LastSeen { get; init; }
    public double? FirstSeen { get; init; }
    public byte? SignalPeak { get; init; }
    public long MsgCount { get; init; }

    public double? DistanceKm { get; init; }

    /// <summary>True when this aircraft was received from a P2P relay peer
    /// rather than directly from the local BEAST feed. Null (omitted in JSON)
    /// for locally-observed aircraft.</summary>
    public bool? Peer { get; init; }

    // Enrichment fields populated by the snapshot pusher from the external
    // clients. Names match the wire schema the frontend reads directly:
    // `origin`, `destination`, `phase`, `operator`, `operator_iata`,
    // `operator_alliance`, `operator_country`, `country_iso`, `manufacturer`.
    public string? Origin { get; init; }
    public string? Destination { get; init; }
    public SnapshotAirport? OriginInfo { get; init; }
    public SnapshotAirport? DestInfo { get; init; }
    public string? Phase { get; init; }
    public string? Operator { get; init; }
    public string? OperatorIata { get; init; }
    public string? OperatorAlliance { get; init; }
    public string? OperatorCountry { get; init; }
    public string? CountryIso { get; init; }
    public string? Manufacturer { get; init; }

    /// <summary>
    /// Comm-B (DF 20/21 Enhanced Mode S) values decoded from the last
    /// unambiguous reply of each BDS register. Sparse — only present when
    /// the aircraft is being interrogated by a Mode S ground radar close
    /// enough to reach the receiver too. Nested so the frontend can detect
    /// "any Comm-B field present" with a single null check.
    /// </summary>
    public SnapshotCommB? CommB { get; init; }

    public IReadOnlyList<SnapshotTrailPoint> Trail { get; init; } = Array.Empty<SnapshotTrailPoint>();
}

/// <summary>Comm-B decoded values, all nullable. <c>_at</c> fields are
/// unix-second timestamps of the last decode for that register — the
/// frontend uses them to age out stale values.</summary>
public sealed record SnapshotCommB
{
    // BDS 4,0
    public int? SelectedAltitudeMcpFt { get; init; }
    public int? SelectedAltitudeFmsFt { get; init; }
    public double? QnhHpa { get; init; }
    public double? Bds40At { get; init; }

    // BDS 4,4
    public int? WindSpeedKt { get; init; }
    public double? WindDirectionDeg { get; init; }
    public double? StaticAirTemperatureC { get; init; }
    /// <summary>"observed" when SAT comes straight from BDS 4,4; "derived"
    /// when computed from TAS (BDS 5,0) and Mach (BDS 6,0). Null when the
    /// value is absent. Lets the frontend flag derived temps so the reader
    /// knows they're coarser than a direct reading.</summary>
    public string? StaticAirTemperatureSource { get; init; }
    public double? TotalAirTemperatureC { get; init; }
    public int? StaticPressureHpa { get; init; }
    public int? Turbulence { get; init; }
    public double? HumidityPct { get; init; }
    public double? Bds44At { get; init; }

    // BDS 5,0
    public double? RollDeg { get; init; }
    public double? TrueTrackDeg { get; init; }
    public int? GroundspeedKt { get; init; }
    public double? TrackRateDegPerS { get; init; }
    public int? TrueAirspeedKt { get; init; }
    public double? Bds50At { get; init; }

    // BDS 6,0
    public double? MagneticHeadingDeg { get; init; }
    public int? IndicatedAirspeedKt { get; init; }
    public double? Mach { get; init; }
    public int? BaroVerticalRateFpm { get; init; }
    public int? InertialVerticalRateFpm { get; init; }
    public double? Bds60At { get; init; }
}

/// <summary>Trimmed airport record for snapshot enrichment.</summary>
public sealed record SnapshotAirport(
    string Icao,
    string Name,
    string? City,
    string? Country,
    double Lat,
    double Lon);

/// <summary>Compact trail-point projection for snapshot serialisation.
/// Serialises as a 5-element positional array <c>[lat, lon, altitude, speed, gap]</c>
/// (see <see cref="SnapshotTrailPointConverter"/>) — the frontend indexes
/// trail points by position.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SnapshotTrailPointConverter))]
public readonly record struct SnapshotTrailPoint(
    double Lat,
    double Lon,
    int? Altitude,
    double? Speed,
    bool Gap);
