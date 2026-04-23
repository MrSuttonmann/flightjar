using FlightJar.Api.Hosting;
using FlightJar.Clients.OpenAip;

namespace FlightJar.Api.Tests;

public class OpenAipPrewarmWorkerTests
{
    [Fact]
    public void TileGrid_CoversTheDiscAround_Receiver_OnGrid()
    {
        // Receiver at 52.98 N, -1.20 E (East Midlands, UK). A 300 km disc
        // at 53° N is ~2.7° lat × ~4.5° lon — expect tiles covering that
        // range aligned to the 2° grid the client snaps to.
        var tiles = OpenAipPrewarmWorker.EnumerateTiles(52.98, -1.20, 300.0).ToList();

        Assert.NotEmpty(tiles);
        foreach (var (mnLat, mnLon, mxLat, mxLon) in tiles)
        {
            // Tiles must be aligned to the client's 2° grid so enumeration
            // hits the same cache key as an on-demand user request.
            Assert.Equal(0.0, mnLat % OpenAipClient.BboxGridDegrees, 9);
            Assert.Equal(0.0, mnLon % OpenAipClient.BboxGridDegrees, 9);
            Assert.Equal(OpenAipClient.BboxGridDegrees, mxLat - mnLat, 9);
            Assert.Equal(OpenAipClient.BboxGridDegrees, mxLon - mnLon, 9);
        }

        // The receiver's own tile must be one of the enumerated ones.
        Assert.Contains(tiles, t =>
            52.98 >= t.mnLat && 52.98 < t.mxLat
            && -1.20 >= t.mnLon && -1.20 < t.mxLon);
    }

    [Fact]
    public void Radius_Zero_Returns_Empty_Enumeration()
    {
        // Disabled prewarm uses radius 0; must yield nothing rather than
        // the single receiver-tile by accident.
        var tiles = OpenAipPrewarmWorker.EnumerateTiles(52.98, -1.20, 0).ToList();
        Assert.Empty(tiles);
    }

    [Fact]
    public void PolarReceiver_DoesNotBlowUpTheLongitudeRadius()
    {
        // At 89° N the cos(lat) lon-scaling approaches zero; with no floor
        // the lon radius would explode to full world width. The clamp keeps
        // the count finite.
        var tiles = OpenAipPrewarmWorker.EnumerateTiles(89.0, 0.0, 300.0).ToList();
        Assert.NotEmpty(tiles);
        Assert.True(tiles.Count < 200, $"expected bounded tile count, got {tiles.Count}");
    }
}
