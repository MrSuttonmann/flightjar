namespace FlightJar.Clients.Adsbdb;

/// <summary>Per-tail record returned by adsbdb for an ICAO24 hex.</summary>
public sealed record AircraftRecord
{
    public string? Registration { get; init; }
    public string? Type { get; init; }
    public string? IcaoType { get; init; }
    public string? Manufacturer { get; init; }
    public string? Operator { get; init; }
    public string? OperatorCountry { get; init; }
    public string? OperatorCountryIso { get; init; }
    public string? PhotoUrl { get; init; }
    public string? PhotoThumbnail { get; init; }
}
