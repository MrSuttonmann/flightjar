namespace FlightJar.Clients.Adsbdb;

/// <summary>Route data returned by adsbdb for a callsign.</summary>
public sealed record RouteInfo(string? Origin, string? Destination, string Callsign);
