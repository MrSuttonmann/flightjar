namespace FlightJar.Core.State;

/// <summary>
/// Lookup from ICAO24 hex to registration / type metadata. Implemented by
/// the reference-data loader (Phase 2). The registry uses it only for
/// snapshot enrichment.
/// </summary>
public interface IAircraftDb
{
    AircraftDbEntry? Lookup(string icao);
}

public readonly record struct AircraftDbEntry(
    string? Registration,
    string? TypeIcao,
    string? TypeLong);
