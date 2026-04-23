using System.Net;
using System.Net.Http.Json;
using FlightJar.Clients.Metar;
using FlightJar.Clients.Tests.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightJar.Clients.Tests.Metar;

public class MetarClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 23, 12, 0, 0, TimeSpan.Zero);

    private static (MetarClient Client, MockHttpMessageHandler Handler, FakeTimeProvider Time) MakeClient(
        string? cachePath = null, bool enabled = true)
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var time = new FakeTimeProvider(T0) { AutoAdvanceAmount = TimeSpan.FromSeconds(3) };
        var client = new MetarClient(http, NullLogger<MetarClient>.Instance, time, cachePath, enabled);
        return (client, handler, time);
    }

    private static HttpResponseMessage MetarResponse(params (string Icao, string Raw)[] entries)
    {
        var body = entries.Select(e => new
        {
            icaoId = e.Icao,
            rawOb = e.Raw,
            obsTime = 1714000000L,
            wdir = 270,
            wspd = 10,
            visib = "10+",
            temp = 15.0,
            dewp = 8.0,
            altim = 1013.25,
            clouds = new[] { new { cover = "SCT" }, new { cover = "BKN" } },
        }).ToArray();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(body),
        };
    }

    [Fact]
    public async Task Disabled_SkipsUpstream()
    {
        var (c, handler, _) = MakeClient(enabled: false);
        var result = await c.LookupManyAsync(new[] { "EGLL" });
        Assert.Empty(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Lookup_ReturnsMetar()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => MetarResponse(("EGLL", "EGLL 120000Z 27010KT 10SM SCT030 BKN050 15/08 Q1013"));
        var result = await c.LookupAsync("EGLL");
        Assert.NotNull(result);
        Assert.StartsWith("EGLL", result!.Raw);
        Assert.Equal("BKN", result.Cover); // headline cover picks BKN over SCT
    }

    [Fact]
    public async Task LookupMany_BatchesInOneRequest()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => MetarResponse(
            ("EGLL", "EGLL raw"), ("EGKK", "EGKK raw"), ("KJFK", "KJFK raw"));
        var result = await c.LookupManyAsync(new[] { "EGLL", "EGKK", "KJFK" });
        Assert.Equal(3, result.Count);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("EGLL raw", result["EGLL"]!.Raw);
    }

    [Fact]
    public async Task LookupMany_MixedCacheHitsAndFetch()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => MetarResponse(("EGLL", "EGLL raw"));
        await c.LookupAsync("EGLL"); // populate cache

        handler.Handler = _ => MetarResponse(("EGKK", "EGKK raw"));
        var result = await c.LookupManyAsync(new[] { "EGLL", "EGKK" });
        Assert.Equal(2, result.Count);
        Assert.Equal("EGLL raw", result["EGLL"]!.Raw);
        Assert.Equal("EGKK raw", result["EGKK"]!.Raw);
        Assert.Equal(2, handler.CallCount); // one per distinct batch
    }

    [Fact]
    public async Task UnknownAirport_GetsCachedNegative()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => MetarResponse(); // empty response
        var result = await c.LookupManyAsync(new[] { "XXXX" });
        Assert.Single(result);
        Assert.Null(result["XXXX"]);
        var cached = c.LookupCached("XXXX");
        Assert.True(cached.Known);
        Assert.Null(cached.Data);
    }

    [Fact]
    public async Task IcaoNormalisation_LowercaseAndLength()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => MetarResponse(("EGLL", "EGLL raw"));
        await c.LookupAsync("egll");
        await c.LookupAsync("EGLL");
        Assert.Equal(1, handler.CallCount);
        Assert.True(c.LookupCached("EGLL").Known);
        Assert.Equal((false, null), c.LookupCached("ab"));      // too short
        Assert.Equal((false, null), c.LookupCached("abcde"));   // too long
        Assert.Equal((false, null), c.LookupCached("E-LL"));    // non-alphanumeric
    }

    [Fact]
    public async Task FourTwoNine_SetsCooldown()
    {
        var (c, handler, time) = MakeClient();
        handler.Handler = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.TryAddWithoutValidation("Retry-After", "90");
            return r;
        };
        await c.LookupAsync("EGLL");
        var diff = c.Throttle.CooldownUntil - time.GetUtcNow();
        Assert.InRange(diff.TotalSeconds, 80, 100);
    }

    [Fact]
    public async Task CachePersistsAndReloads()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"flightjar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var path = Path.Combine(tmp, "metar.json.gz");
        try
        {
            var (c1, h1, _) = MakeClient(cachePath: path);
            h1.Handler = _ => MetarResponse(("EGLL", "EGLL raw"));
            await c1.LookupAsync("EGLL");

            var (c2, _, _) = MakeClient(cachePath: path);
            await c2.LoadCacheAsync();
            var cached = c2.LookupCached("EGLL");
            Assert.True(cached.Known);
            Assert.Equal("EGLL raw", cached.Data!.Raw);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task HeadlineCover_PicksMaxRank()
    {
        var (c, handler, _) = MakeClient();
        // Multiple cloud layers — SKC < FEW < SCT < BKN < OVC.
        handler.Handler = _ =>
        {
            var body = new[] { new
            {
                icaoId = "EGLL",
                rawOb = "EGLL raw",
                clouds = new[]
                {
                    new { cover = "FEW" },
                    new { cover = "OVC" },
                    new { cover = "SCT" },
                },
            } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        };
        var result = await c.LookupAsync("EGLL");
        Assert.Equal("OVC", result!.Cover);
    }
}
