using System.Net;
using System.Net.Http.Json;
using FlightJar.Clients.Adsbdb;
using FlightJar.Clients.Tests.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightJar.Clients.Tests.Adsbdb;

public class AdsbdbClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 23, 12, 0, 0, TimeSpan.Zero);

    private static (AdsbdbClient Client, MockHttpMessageHandler Handler, FakeTimeProvider Time) MakeClient(
        string? cachePath = null, bool enabled = true)
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        // AutoAdvance > MinRequestInterval makes the throttle's wait go negative
        // between calls — otherwise Task.Delay on the fake clock hangs.
        var time = new FakeTimeProvider(T0)
        {
            AutoAdvanceAmount = TimeSpan.FromSeconds(2),
        };
        var client = new AdsbdbClient(http, NullLogger<AdsbdbClient>.Instance, time, cachePath, enabled);
        return (client, handler, time);
    }

    private static HttpResponseMessage RouteResponse(string? origin = "EGLL", string? destination = "KJFK", string? callsign = "BAW1")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                response = new
                {
                    flightroute = new
                    {
                        callsign,
                        origin = origin is null ? null : new { icao_code = origin },
                        destination = destination is null ? null : new { icao_code = destination },
                    },
                },
            }),
        };
    }

    private static HttpResponseMessage AircraftResponse(string registration = "G-EZAN")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                response = new
                {
                    aircraft = new
                    {
                        registration,
                        type = "A319-111",
                        icao_type = "A319",
                        manufacturer = "Airbus Sas",
                        registered_owner = "easyJet Airline",
                        registered_owner_country_name = "United Kingdom",
                        registered_owner_country_iso_name = "GB",
                        url_photo = "https://airport-data.com/images/aircraft/001.jpg",
                        url_photo_thumbnail = "https://airport-data.com/images/aircraft/thumb/001.jpg",
                    },
                },
            }),
        };
    }

    [Fact]
    public async Task DisabledFlag_IsRespected()
    {
        var (c, handler, _) = MakeClient(enabled: false);
        Assert.False(c.Enabled);
        Assert.Null(await c.LookupRouteAsync("BAW1"));
        Assert.Null(await c.LookupAircraftAsync("abcdef"));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Route_EmptyCallsign_ReturnsNull()
    {
        var (c, handler, _) = MakeClient();
        Assert.Null(await c.LookupRouteAsync(""));
        Assert.Null(await c.LookupRouteAsync("   "));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Route_CacheHit_SkipsUpstream()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => RouteResponse();
        var first = await c.LookupRouteAsync("BAW1");
        var second = await c.LookupRouteAsync("BAW1");
        Assert.Equal("EGLL", first!.Origin);
        Assert.Equal("EGLL", second!.Origin);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Route_CallsignKeyIsNormalised()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => RouteResponse();
        await c.LookupRouteAsync("  baw1 ");
        await c.LookupRouteAsync("BAW1");
        Assert.Equal(1, handler.CallCount);
        Assert.True(c.LookupCachedRoute("BAW1").Known);
    }

    [Fact]
    public async Task Route_NegativeCachedShorterThanPositive()
    {
        var (c, handler, time) = MakeClient();
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await c.LookupRouteAsync("UNKNWN");
        // After negative TTL (1h + 1s), cache is stale.
        time.Advance(AdsbdbClient.RouteNegativeTtl + TimeSpan.FromSeconds(1));
        Assert.False(c.LookupCachedRoute("UNKNWN").Known);
    }

    [Fact]
    public async Task Aircraft_RejectsBadHex()
    {
        var (c, handler, _) = MakeClient();
        Assert.Null(await c.LookupAircraftAsync("xyz"));
        Assert.Null(await c.LookupAircraftAsync("0123456"));
        Assert.Null(await c.LookupAircraftAsync(""));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Aircraft_CacheHit_CaseInsensitive()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => AircraftResponse();
        var first = await c.LookupAircraftAsync("400db1");
        var second = await c.LookupAircraftAsync("400DB1");
        Assert.Equal("G-EZAN", first!.Registration);
        Assert.Equal("G-EZAN", second!.Registration);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Routes_AndAircraft_DoNotCollideByKey()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("/callsign/"))
            {
                return RouteResponse(callsign: "ABCDEF");
            }
            return AircraftResponse(registration: "N123AB");
        };
        await c.LookupAircraftAsync("abcdef");
        await c.LookupRouteAsync("ABCDEF");
        Assert.Equal("N123AB", c.LookupCachedAircraft("abcdef").Data!.Registration);
        Assert.Equal("EGLL", c.LookupCachedRoute("ABCDEF").Data!.Origin);
    }

    [Fact]
    public async Task UpstreamFailure_FallsBackToStaleCache()
    {
        var (c, handler, time) = MakeClient();
        handler.Handler = _ => RouteResponse(origin: "KSFO", destination: "PHNL", callsign: "UAL1");
        await c.LookupRouteAsync("UAL1");
        // Expire the entry then cause a failure.
        time.Advance(AdsbdbClient.RoutePositiveTtl + TimeSpan.FromSeconds(1));
        handler.Handler = _ => throw new HttpRequestException("boom");
        var result = await c.LookupRouteAsync("UAL1");
        Assert.Equal("KSFO", result!.Origin);
    }

    [Fact]
    public async Task Cache_PersistsBothBucketsAndLoads()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"flightjar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var path = Path.Combine(tmp, "flight_routes.json.gz");
        try
        {
            var (c1, h1, _) = MakeClient(cachePath: path);
            h1.Handler = req =>
            {
                if (req.RequestUri!.AbsoluteUri.Contains("/callsign/"))
                {
                    return RouteResponse(origin: "LFPG", destination: "RJTT", callsign: "AFR1");
                }
                return AircraftResponse(registration: "F-AAAA");
            };
            await c1.LookupRouteAsync("AFR1");
            await c1.LookupAircraftAsync("abcdef");

            var (c2, _, _) = MakeClient(cachePath: path);
            await c2.LoadCacheAsync();
            var cachedRoute = c2.LookupCachedRoute("AFR1");
            Assert.True(cachedRoute.Known);
            Assert.Equal("LFPG", cachedRoute.Data!.Origin);
            var cachedAircraft = c2.LookupCachedAircraft("abcdef");
            Assert.True(cachedAircraft.Known);
            Assert.Equal("F-AAAA", cachedAircraft.Data!.Registration);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task FourTwoNine_BlocksBothEndpoints()
    {
        var (c, handler, time) = MakeClient();
        handler.Handler = req =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("/callsign/"))
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }
            return AircraftResponse();
        };
        // First call hits 429
        Assert.Null(await c.LookupRouteAsync("BAW1"));
        // Aircraft call should be suppressed by the shared cooldown
        var result = await c.LookupAircraftAsync("abcdef");
        Assert.Null(result);
        Assert.Equal(1, handler.CallCount); // aircraft was never fetched
        Assert.True(c.Throttle.CooldownUntil > time.GetUtcNow());
    }

    [Fact]
    public async Task FourTwoNine_RespectsRetryAfterHeader()
    {
        var (c, handler, time) = MakeClient();
        handler.Handler = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.TryAddWithoutValidation("Retry-After", "30");
            return r;
        };
        await c.LookupRouteAsync("BAW1");
        var diff = c.Throttle.CooldownUntil - time.GetUtcNow();
        Assert.InRange(diff.TotalSeconds, 25, 35);
    }

    [Fact]
    public async Task ExpiredEntries_DroppedOnReload()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"flightjar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var path = Path.Combine(tmp, "flight_routes.json.gz");
        try
        {
            // Round-trip one entry via the first client, then advance time past expiry.
            var (c1, h1, time1) = MakeClient(cachePath: path);
            h1.Handler = req => req.RequestUri!.AbsoluteUri.Contains("/callsign/")
                ? RouteResponse(origin: "Y")
                : AircraftResponse(registration: "Y");
            await c1.LookupRouteAsync("FRESH");
            await c1.LookupAircraftAsync("abcdef");

            // Now push far into the future where both entries are stale and reload.
            var (c2, _, time2) = MakeClient(cachePath: path);
            time2.Advance(TimeSpan.FromDays(365));
            await c2.LoadCacheAsync();
            Assert.False(c2.LookupCachedRoute("FRESH").Known);
            Assert.False(c2.LookupCachedAircraft("abcdef").Known);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task LookupCachedRoute_DistinguishesMissFromNegative()
    {
        var (c, handler, _) = MakeClient();
        // Unknown key → known=false
        var miss = c.LookupCachedRoute("MISSING");
        Assert.False(miss.Known);
        // Cached negative → known=true, data=null
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await c.LookupRouteAsync("UNKNWN");
        var neg = c.LookupCachedRoute("UNKNWN");
        Assert.True(neg.Known);
        Assert.Null(neg.Data);
        // Cached positive → known=true, data=record
        handler.Handler = _ => RouteResponse();
        await c.LookupRouteAsync("BAW1");
        var pos = c.LookupCachedRoute("BAW1");
        Assert.True(pos.Known);
        Assert.Equal("EGLL", pos.Data!.Origin);
    }

    [Fact]
    public void LookupCached_TreatsBadKeyAsMiss()
    {
        var (c, _, _) = MakeClient();
        Assert.Equal((false, null), c.LookupCachedRoute(""));
        Assert.Equal((false, null), c.LookupCachedAircraft("xyz"));
        Assert.Equal((false, null), c.LookupCachedAircraft("0123456"));
    }

    [Fact]
    public async Task OldSchema_Ignored()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"flightjar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var path = Path.Combine(tmp, "flight_routes.json.gz");
        try
        {
            // Write a v2-shaped cache file.
            await using (var file = File.Create(path))
            await using (var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
            {
                await System.Text.Json.JsonSerializer.SerializeAsync(gz, new
                {
                    version = 2,
                    cache = new Dictionary<string, object>
                    {
                        ["BAW1"] = new { data = new { origin = "EGLL" }, expires_at = 1_999_999_999L },
                    },
                });
            }
            var (c, _, _) = MakeClient(cachePath: path);
            await c.LoadCacheAsync();
            Assert.False(c.LookupCachedRoute("BAW1").Known);
            Assert.False(c.LookupCachedAircraft("anykey").Known);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
