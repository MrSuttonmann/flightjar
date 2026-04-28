using FlightJar.Clients.Adsbdb;
using FlightJar.Clients.Metar;
using FlightJar.Core.ReferenceData;

namespace FlightJar.Api.Hosting;

/// <summary>
/// Optional snapshot-enrichment dependencies the <see cref="RegistryWorker"/>
/// uses to fold origin/destination/airline/airport metadata into each tick's
/// snapshot. All nullable: when a member is null, the corresponding
/// enrichment step is skipped.
/// </summary>
public sealed record RegistrySnapshotEnrichers(
    AdsbdbClient? Adsbdb = null,
    MetarClient? Metar = null,
    AirportsDb? Airports = null,
    AirlinesDb? Airlines = null);
