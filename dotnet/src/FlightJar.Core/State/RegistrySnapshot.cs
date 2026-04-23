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

    public static RegistrySnapshot Empty { get; } = new(
        Now: 0, Count: 0, Positioned: 0,
        Receiver: null, SiteName: null,
        Aircraft: Array.Empty<SnapshotAircraft>());
}

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
    public double? WindDir { get; init; }
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

    public IReadOnlyList<SnapshotTrailPoint> Trail { get; init; } = Array.Empty<SnapshotTrailPoint>();
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
