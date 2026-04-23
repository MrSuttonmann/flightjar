namespace FlightJar.Core.State;

/// <summary>
/// One recorded position fix in an aircraft's trail history. <c>Gap</c> flags
/// that the segment from the previous trail point to this one spanned a
/// signal-lost period (&gt; <see cref="AircraftRegistry.SignalLostMinAge"/>
/// between real fixes); the frontend renders those segments as dashed black.
/// </summary>
public readonly record struct TrailPoint(
    double Lat,
    double Lon,
    int? Altitude,
    double? Speed,
    double Timestamp,
    bool Gap);
