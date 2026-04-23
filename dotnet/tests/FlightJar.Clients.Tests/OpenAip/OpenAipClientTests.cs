using System.Net;
using System.Net.Http.Json;
using FlightJar.Clients.OpenAip;
using FlightJar.Clients.Tests.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightJar.Clients.Tests.OpenAip;

public class OpenAipClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 23, 12, 0, 0, TimeSpan.Zero);

    private static (OpenAipClient Client, MockHttpMessageHandler Handler, FakeTimeProvider Time) MakeClient(
        string? cachePath = null, string? apiKey = "test-key")
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var time = new FakeTimeProvider(T0)
        {
            AutoAdvanceAmount = TimeSpan.FromSeconds(2),
        };
        var client = new OpenAipClient(
            http, NullLogger<OpenAipClient>.Instance, time, cachePath, apiKey);
        return (client, handler, time);
    }

    private static HttpResponseMessage AirspaceListResponse(
        params (string id, int type, int icaoClass, int lowerFt, int upperFt)[] items)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                page = 1,
                limit = 500,
                totalCount = items.Length,
                totalPages = 1,
                items = items.Select(i => new
                {
                    _id = i.id,
                    name = $"AS-{i.id}",
                    type = i.type,
                    icaoClass = i.icaoClass,
                    lowerLimit = new { value = i.lowerFt, unit = 1, referenceDatum = 0 },
                    upperLimit = new { value = i.upperFt, unit = 1, referenceDatum = 1 },
                    geometry = new
                    {
                        type = "Polygon",
                        coordinates = new[] { new[] {
                            new[] { 0.0, 50.0 }, new[] { 2.0, 50.0 },
                            new[] { 2.0, 52.0 }, new[] { 0.0, 52.0 },
                            new[] { 0.0, 50.0 },
                        } },
                    },
                }).ToArray(),
            }),
        };
    }

    private static HttpResponseMessage ObstacleListResponse(params (string id, double lat, double lon, int heightFt)[] items)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                page = 1,
                limit = 500,
                totalCount = items.Length,
                totalPages = 1,
                items = items.Select(i => new
                {
                    _id = i.id,
                    name = $"OBS-{i.id}",
                    type = 4,
                    height = new { value = i.heightFt, unit = 1, referenceDatum = 0 },
                    elevation = new { value = 100, unit = 1, referenceDatum = 1 },
                    geometry = new { type = "Point", coordinates = new[] { i.lon, i.lat } },
                }).ToArray(),
            }),
        };
    }

    [Fact]
    public async Task DisabledWhenNoApiKey()
    {
        var (c, handler, _) = MakeClient(apiKey: null);
        Assert.False(c.Enabled);
        var r = await c.GetAirspacesAsync(50, 0, 52, 2);
        Assert.Empty(r);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void BboxKey_TilesForBbox_FullyInsideOneTile_ReturnsThatTile()
    {
        // A viewport entirely inside one 2° cell should map to a single
        // key — the whole point of per-tile caching is that small pans
        // within the same tile hit the same entry.
        var tiles = BboxKey.TilesForBbox(50.3, 0.2, 51.9, 1.7).ToList();
        var tile = Assert.Single(tiles);
        Assert.Equal(50.0, tile.MinLat);
        Assert.Equal(0.0, tile.MinLon);
        Assert.Equal(52.0, tile.MaxLat);
        Assert.Equal(2.0, tile.MaxLon);
    }

    [Fact]
    public void BboxKey_TilesForBbox_CrossingABoundary_YieldsMultipleTiles()
    {
        // Previously this case silently re-fetched from upstream because
        // the snapped outer bbox produced a fresh key for each pan.
        // Under per-tile caching, the viewport enumerates both tiles and
        // the client looks each up independently.
        var tiles = BboxKey.TilesForBbox(50.5, -0.5, 53.5, 1.5)
            .OrderBy(t => t.MinLat).ThenBy(t => t.MinLon)
            .ToList();
        Assert.Equal(
            new[]
            {
                new BboxKey(50.0, -2.0), new BboxKey(50.0, 0.0),
                new BboxKey(52.0, -2.0), new BboxKey(52.0, 0.0),
            },
            tiles);
    }

    [Fact]
    public void BboxKey_TilesForBbox_ClampsToPolarLatitude()
    {
        var tiles = BboxKey.TilesForBbox(89.5, -179.5, 90, -178).ToList();
        Assert.NotEmpty(tiles);
        Assert.All(tiles, t => Assert.InRange(t.MinLat, -90.0, 88.0));
        Assert.All(tiles, t => Assert.InRange(t.MaxLat, -88.0, 90.0));
    }

    [Fact]
    public async Task Airspaces_CacheHit_SkipsUpstream()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => AirspaceListResponse(("a1", 4, 3, 0, 2500));
        var r1 = await c.GetAirspacesAsync(50.3, 0.2, 51.9, 1.7);
        var r2 = await c.GetAirspacesAsync(50.3, 0.2, 51.9, 1.7);
        Assert.Single(r1);
        Assert.Single(r2);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("D", r1[0].Class);
        Assert.Equal("CTR", r1[0].TypeName);
        Assert.Equal(0, r1[0].LowerFt);
        Assert.Equal(2500, r1[0].UpperFt);
        Assert.Equal("GND", r1[0].LowerDatum);
        Assert.Equal("MSL", r1[0].UpperDatum);
    }

    [Fact]
    public async Task Airspaces_SamplePanHitsSameCacheKey()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => AirspaceListResponse(("a1", 4, 3, 0, 2500));
        await c.GetAirspacesAsync(50.3, 0.2, 51.9, 1.7);
        // Small pan within the same snapped 2° bbox — must reuse cache.
        await c.GetAirspacesAsync(50.5, 0.5, 51.5, 1.5);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Airspaces_LargerPanMisses()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => AirspaceListResponse(("a1", 4, 3, 0, 2500));
        await c.GetAirspacesAsync(50.3, 0.2, 51.9, 1.7);
        await c.GetAirspacesAsync(58.0, 10.0, 60.0, 12.0);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Airspaces_FiltersToExactBbox()
    {
        var (c, handler, _) = MakeClient();
        // OpenAIP returns two airspaces; one falls in the snapped bbox but
        // outside the caller's tight bbox (52.0..52.05). Polygon is
        // 0..2 lon / 50..52 lat — should NOT match a caller asking for
        // 52.5..53.0 / 10..12.
        handler.Handler = _ => AirspaceListResponse(("a1", 4, 3, 0, 2500));
        var r = await c.GetAirspacesAsync(52.5, 10.1, 53.0, 12.0);
        Assert.Empty(r);
    }

    [Fact]
    public async Task Obstacles_FlattensPointGeometry()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => ObstacleListResponse(("o1", 51.1, 0.5, 300));
        var r = await c.GetObstaclesAsync(50, 0, 52, 2);
        Assert.Single(r);
        Assert.Equal(0.5, r[0].Lon);
        Assert.Equal(51.1, r[0].Lat);
        Assert.Equal(300, r[0].HeightFt);
        Assert.Equal("Tower", r[0].TypeName);
    }

    [Fact]
    public async Task Obstacles_FiltersOutOfBboxPoints()
    {
        var (c, handler, _) = MakeClient();
        // Two obstacles in the snapped 50..52 / 0..2 box; caller asks a sub-box
        // that only includes one of them.
        handler.Handler = _ => ObstacleListResponse(
            ("o1", 50.2, 0.2, 120),
            ("o2", 51.8, 1.8, 450));
        var r = await c.GetObstaclesAsync(50.0, 0.0, 50.5, 0.5);
        Assert.Single(r);
        Assert.Equal("o1", r[0].Id);
    }

    [Fact]
    public async Task ApiKeyHeaderIsSent()
    {
        var (c, handler, _) = MakeClient(apiKey: "secret123");
        handler.Handler = _ => AirspaceListResponse(("a1", 4, 3, 0, 2500));
        await c.GetAirspacesAsync(50.3, 0.2, 51.9, 1.7);
        var req = handler.Requests.Single();
        Assert.True(req.Headers.TryGetValues("x-openaip-api-key", out var vals));
        Assert.Equal("secret123", vals!.Single());
        Assert.Contains("bbox=0,50,2,52", req.RequestUri!.Query);
    }

    [Fact]
    public async Task Rate429_SetsCooldownAndReturnsStale()
    {
        var (c, handler, time) = MakeClient();
        var seq = 0;
        handler.Handler = _ =>
        {
            seq++;
            if (seq == 1) return AirspaceListResponse(("a1", 4, 3, 0, 2500));
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        };
        await c.GetAirspacesAsync(50.3, 0.2, 51.9, 1.7);
        // Nudge time past the 7-day positive TTL to force a re-fetch that 429s.
        time.Advance(TimeSpan.FromDays(8));
        var r = await c.GetAirspacesAsync(50.3, 0.2, 51.9, 1.7);
        Assert.True(c.Throttle.IsInCooldown(time.GetUtcNow()));
        // Stale entry is still returned as a fallback so the UI keeps working.
        Assert.Single(r);
    }

    [Fact]
    public async Task Pagination_FetchesUntilNextPageNull()
    {
        var (c, handler, _) = MakeClient();
        int call = 0;
        handler.Handler = _ =>
        {
            call++;
            if (call == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        page = 1,
                        limit = 2,
                        totalCount = 3,
                        totalPages = 2,
                        nextPage = 2,
                        items = new[]
                        {
                            new { _id = "o1", type = 4, geometry = new { type = "Point", coordinates = new[] { 0.5, 51.1 } } },
                            new { _id = "o2", type = 4, geometry = new { type = "Point", coordinates = new[] { 0.6, 51.2 } } },
                        },
                    }),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    page = 2,
                    limit = 2,
                    totalCount = 3,
                    totalPages = 2,
                    items = new[]
                    {
                        new { _id = "o3", type = 4, geometry = new { type = "Point", coordinates = new[] { 0.7, 51.3 } } },
                    },
                }),
            };
        };
        var r = await c.GetObstaclesAsync(50, 0, 52, 2);
        Assert.Equal(3, r.Count);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Pagination_PreservesEarlierPagesOnMidRequestFailure()
    {
        var (c, handler, _) = MakeClient();
        int call = 0;
        handler.Handler = _ =>
        {
            call++;
            if (call == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        page = 1,
                        limit = 2,
                        totalCount = 3,
                        totalPages = 2,
                        nextPage = 2,
                        items = new[]
                        {
                            new { _id = "o1", type = 4, geometry = new { type = "Point", coordinates = new[] { 0.5, 51.1 } } },
                            new { _id = "o2", type = 4, geometry = new { type = "Point", coordinates = new[] { 0.6, 51.2 } } },
                        },
                    }),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        };
        var r = await c.GetObstaclesAsync(50, 0, 52, 2);
        // Page 1 succeeded; page 2 429'd. Must still return page 1's items.
        Assert.Equal(2, r.Count);
        Assert.Equal("o1", r[0].Id);
        Assert.Equal("o2", r[1].Id);
    }

    [Fact]
    public async Task MetreUnitConvertsToFeet()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                page = 1,
                limit = 500,
                totalCount = 1,
                totalPages = 1,
                items = new[]
                {
                    new
                    {
                        _id = "a1", type = 7, icaoClass = 2,
                        lowerLimit = new { value = 500, unit = 0, referenceDatum = 0 },
                        upperLimit = new { value = 55, unit = 6, referenceDatum = 2 },
                        geometry = new
                        {
                            type = "Polygon",
                            coordinates = new[] { new[] {
                                new[] { 0.0, 50.0 }, new[] { 2.0, 50.0 },
                                new[] { 2.0, 52.0 }, new[] { 0.0, 52.0 },
                                new[] { 0.0, 50.0 },
                            } },
                        },
                    },
                },
            }),
        };
        var r = await c.GetAirspacesAsync(50.3, 0.2, 51.9, 1.7);
        var a = r.Single();
        // 500 m -> 1640 ft (rounded), FL055 -> 5500 ft with "FL" datum.
        Assert.Equal(1640, a.LowerFt);
        Assert.Equal("GND", a.LowerDatum);
        Assert.Equal(5500, a.UpperFt);
        Assert.Equal("FL", a.UpperDatum);
    }
}
