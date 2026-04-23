namespace FlightJar.Core.State;

/// <summary>Minimal airport view used for route plausibility and phase classification.</summary>
public readonly record struct AirportInfo(double Lat, double Lon);
