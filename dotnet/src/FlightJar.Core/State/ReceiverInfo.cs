namespace FlightJar.Core.State;

/// <summary>
/// Displayed receiver coordinates. May be anonymised relative to the actual
/// receiver location used for CPR decoding.
/// </summary>
public readonly record struct ReceiverInfo(double? Lat, double? Lon, double? AnonKm);
