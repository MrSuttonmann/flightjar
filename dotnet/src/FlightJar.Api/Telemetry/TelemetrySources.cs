using FlightJar.Api.Hosting;
using FlightJar.Core.Stats;
using FlightJar.Persistence.Watchlist;

namespace FlightJar.Api.Telemetry;

/// <summary>
/// Optional aggregate-source dependencies the <see cref="TelemetryWorker"/>
/// reads to populate per-tick / identify payloads. Each one is independently
/// optional: any null member contributes nothing to the payload rather than
/// blocking the ping.
/// </summary>
/// <param name="FrameStats">Provides <c>FrameCount</c> for the per-tick frame
/// delta.</param>
/// <param name="Watchlist">Provides watchlist-size population.</param>
/// <param name="PolarCoverage">Provides max-range / p95 receiver-coverage
/// stats.</param>
/// <param name="TrafficHeatmap">Provides the busiest UTC hour of week.</param>
/// <param name="AircraftDbOverridePath">When set + the file exists, the
/// telemetry payload reports a /data/ aircraft-DB override is in use.</param>
public sealed record TelemetrySources(
    IBeastFrameStats? FrameStats = null,
    WatchlistStore? Watchlist = null,
    PolarCoverage? PolarCoverage = null,
    TrafficHeatmap? TrafficHeatmap = null,
    string? AircraftDbOverridePath = null);
