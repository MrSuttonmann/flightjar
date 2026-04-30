using FlightJar.Core.State;

namespace FlightJar.Core.Tests.State;

public sealed class PeerAircraftCacheTests
{
    private static SnapshotAircraft Make(string icao) =>
        new() { Icao = icao };

    [Fact]
    public void GetFresh_ReturnsAircraftWithinWindow()
    {
        var cache = new PeerAircraftCache();
        var now = 1000.0;
        cache.Update([Make("abc123")], now);

        var result = cache.GetFresh(now + 30, maxAgeS: 65);

        Assert.Single(result);
        Assert.Equal("abc123", result[0].Icao);
    }

    [Fact]
    public void GetFresh_EvictsStaleEntries()
    {
        var cache = new PeerAircraftCache();
        var now = 1000.0;
        cache.Update([Make("abc123")], now);

        var result = cache.GetFresh(now + 100, maxAgeS: 65);

        Assert.Empty(result);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Update_OverwritesExistingEntry()
    {
        var cache = new PeerAircraftCache();
        var t1 = 1000.0;
        var t2 = 1010.0;
        cache.Update([Make("abc123") with { Callsign = "OLD" }], t1);
        cache.Update([Make("abc123") with { Callsign = "NEW" }], t2);

        var result = cache.GetFresh(t2, maxAgeS: 65);

        Assert.Single(result);
        Assert.Equal("NEW", result[0].Callsign);
    }

    [Fact]
    public void GetFresh_IsCaseInsensitiveOnIcao()
    {
        var cache = new PeerAircraftCache();
        var now = 1000.0;
        cache.Update([Make("ABC123")], now);
        cache.Update([Make("abc123") with { Callsign = "LOWER" }], now + 1);

        var result = cache.GetFresh(now + 5, maxAgeS: 65);

        Assert.Single(result);
        Assert.Equal("LOWER", result[0].Callsign);
    }

    [Fact]
    public void GetFresh_MixedFreshnessReturnsOnlyFresh()
    {
        var cache = new PeerAircraftCache();
        cache.Update([Make("fresh1"), Make("fresh2")], 2000.0);
        cache.Update([Make("stale1")], 1000.0);

        var result = cache.GetFresh(nowUnix: 2050.0, maxAgeS: 65);

        var icaos = result.Select(a => a.Icao).ToHashSet();
        Assert.Contains("fresh1", icaos);
        Assert.Contains("fresh2", icaos);
        Assert.DoesNotContain("stale1", icaos);
    }
}
