using System.Diagnostics;
using FlightJar.Clients.OpenAip;
using FlightJar.Core.Configuration;

namespace FlightJar.Api.Hosting;

/// <summary>
/// On startup, prime the <see cref="OpenAipClient"/> disk cache with every
/// tile within <c>OPENAIP_PREFETCH_RADIUS_KM</c> of the receiver. The first
/// user pan then lands in a warm cache instead of stalling on an upstream
/// paginated fetch (up to 8 pages × ~1.2 s throttle spacing per feature
/// type per tile). Runs once at startup; the client's 7-day positive TTL
/// keeps the cache hot across subsequent restarts.
/// </summary>
public sealed class OpenAipPrewarmWorker(
    OpenAipClient client,
    AppOptions options,
    ILogger<OpenAipPrewarmWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!client.Enabled)
        {
            logger.LogInformation("openaip prewarm skipped — OPENAIP_API_KEY not set");
            return;
        }
        if (options.LatRef is not double latRef || options.LonRef is not double lonRef)
        {
            logger.LogInformation("openaip prewarm skipped — LAT_REF / LON_REF not set");
            return;
        }
        var radiusKm = options.OpenAipPrefetchRadiusKm;
        if (radiusKm <= 0)
        {
            logger.LogInformation("openaip prewarm disabled (OPENAIP_PREFETCH_RADIUS_KM = 0)");
            return;
        }

        // Tiny head-start so the first cascade of startup logs lands first.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var tiles = EnumerateTiles(latRef, lonRef, radiusKm).ToList();
        logger.LogInformation(
            "openaip prewarm starting: {N} tiles × 3 feature types around ({Lat:0.###},{Lon:0.###}), radius {R} km",
            tiles.Count, latRef, lonRef, radiusKm);

        var sw = Stopwatch.StartNew();
        int done = 0;
        foreach (var (mnLat, mnLon, mxLat, mxLon) in tiles)
        {
            if (stoppingToken.IsCancellationRequested) return;
            try
            {
                // Each call trips the client's tile-level cache: a miss
                // triggers the paginated upstream fetch + persists the
                // result to /data/openaip.json.gz. Hits are a no-op.
                _ = await client.GetAirspacesAsync(mnLat, mnLon, mxLat, mxLon, stoppingToken);
                _ = await client.GetObstaclesAsync(mnLat, mnLon, mxLat, mxLon, stoppingToken);
                _ = await client.GetReportingPointsAsync(mnLat, mnLon, mxLat, mxLon, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // One bad tile shouldn't poison the rest of the prewarm.
                // Upstream 5xx on a specific bbox happens; keep going.
                logger.LogWarning(ex,
                    "openaip prewarm: tile ({MnLat:0.###},{MnLon:0.###})-({MxLat:0.###},{MxLon:0.###}) failed, continuing",
                    mnLat, mnLon, mxLat, mxLon);
            }
            done++;
            if (done % 4 == 0 || done == tiles.Count)
            {
                logger.LogInformation(
                    "openaip prewarm progress: {Done}/{Total} tiles",
                    done, tiles.Count);
            }
        }
        logger.LogInformation(
            "openaip prewarm complete: {N} tiles in {Seconds:0.0} s",
            tiles.Count, sw.Elapsed.TotalSeconds);
    }

    /// <summary>Enumerate 2° tiles covering the disc of radius
    /// <paramref name="radiusKm"/> around (<paramref name="latRef"/>,
    /// <paramref name="lonRef"/>). Aligned to <see cref="OpenAipClient.BboxGridDegrees"/>
    /// so the bbox keys match what <see cref="BboxKey.Snap"/> would compute
    /// for a user request — same tile, same cache entry.</summary>
    internal static IEnumerable<(double mnLat, double mnLon, double mxLat, double mxLon)> EnumerateTiles(
        double latRef, double lonRef, double radiusKm)
    {
        // Radius <= 0 means prewarm is disabled; match the worker's own
        // short-circuit so the tile list is empty rather than a single
        // receiver tile (which would fire one request per feature type
        // every startup).
        if (radiusKm <= 0) yield break;

        const double grid = OpenAipClient.BboxGridDegrees;
        var latRadius = radiusKm / 111.0;
        // Longitude degrees shrink with cos(lat). Clamp the cosine at 0.1
        // so polar latitudes don't blow the lon radius out to ±180.
        var cosLat = Math.Max(0.1, Math.Cos(latRef * Math.PI / 180.0));
        var lonRadius = radiusKm / (111.0 * cosLat);

        var minLat = Math.Max(-90, Math.Floor((latRef - latRadius) / grid) * grid);
        var maxLat = Math.Min(90, Math.Ceiling((latRef + latRadius) / grid) * grid);
        var minLon = Math.Floor((lonRef - lonRadius) / grid) * grid;
        var maxLon = Math.Ceiling((lonRef + lonRadius) / grid) * grid;

        for (var lat = minLat; lat < maxLat; lat += grid)
        {
            for (var lon = minLon; lon < maxLon; lon += grid)
            {
                yield return (lat, lon, lat + grid, lon + grid);
            }
        }
    }
}
