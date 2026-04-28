using FlightJar.Core.Stats;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Optional per-tick observers the <see cref="RegistryWorker"/> hooks into
/// the registry: polar coverage (max range per bearing), traffic heatmap
/// (weekday × hour), and polar heatmap (per bearing, all observations).
/// All nullable; null members opt out of their observation pass and the
/// debounced persist call.
/// </summary>
public sealed record RegistryStatsCollectors(
    PolarCoverage? PolarCoverage = null,
    TrafficHeatmap? TrafficHeatmap = null,
    PolarHeatmap? PolarHeatmap = null);
