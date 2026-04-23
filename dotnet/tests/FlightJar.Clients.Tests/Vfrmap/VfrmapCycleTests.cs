using System.Net;
using FlightJar.Clients.Tests.Mocks;
using FlightJar.Clients.Vfrmap;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightJar.Clients.Tests.Vfrmap;

public class VfrmapCycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("20260410", true)]
    [InlineData("20091231", false)] // before MinYear 2010
    [InlineData("20501231", false)] // too far in future
    [InlineData("not-a-date", false)]
    [InlineData("20260230", false)] // invalid day
    public void IsValidCycle(string candidate, bool expected)
    {
        Assert.Equal(expected, VfrmapCycle.IsValidCycle(candidate));
    }

    [Fact]
    public void ExtractMapJsPath_FindsPathWithCacheBuster()
    {
        var html = """
            <html><head>
            <script src="js/map.js?v=123"></script>
            </head></html>
            """;
        Assert.Equal("js/map.js?v=123", VfrmapCycle.ExtractMapJsPath(html));
    }

    [Fact]
    public void ExtractMapJsPath_ReturnsNullWhenMissing()
    {
        Assert.Null(VfrmapCycle.ExtractMapJsPath("<html></html>"));
    }

    [Fact]
    public void ExtractCycle_PicksNewestValidDate()
    {
        var js = """
            var f='20260410';
            var old='20250601';  // older cycle in comments
            var future='20501231';  // invalid (too far)
            """;
        Assert.Equal("20260410", VfrmapCycle.ExtractCycle(js));
    }

    [Fact]
    public void ExtractCycle_ReturnsNullWhenNoValidDates()
    {
        Assert.Null(VfrmapCycle.ExtractCycle("var f='not a date';"));
    }

    [Fact]
    public async Task Discover_ScrapesHomepageAndMapJs()
    {
        var handler = new MockHttpMessageHandler();
        handler.Handler = req =>
        {
            if (req.RequestUri!.AbsoluteUri == "https://vfrmap.com/")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        <html><script src="js/map.js?v=5"></script></html>
                        """),
                };
            }
            if (req.RequestUri.AbsoluteUri.Contains("js/map.js"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("var f='20260410';"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        var http = new HttpClient(handler);
        var v = new VfrmapCycle(http, NullLogger<VfrmapCycle>.Instance, new FakeTimeProvider(T0));
        var cycle = await v.DiscoverAsync();
        Assert.Equal("20260410", cycle);
        Assert.Equal("20260410", v.CurrentDate);
    }

    [Fact]
    public async Task Discover_ReturnsNullOnFailure_KeepsExistingDate()
    {
        var handler = new MockHttpMessageHandler();
        // First call succeeds, cycle = 20260410
        handler.Handler = req =>
        {
            if (req.RequestUri!.AbsoluteUri == "https://vfrmap.com/")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""<script src="js/map.js"></script>"""),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("var f='20260410';"),
            };
        };
        var http = new HttpClient(handler);
        var v = new VfrmapCycle(http, NullLogger<VfrmapCycle>.Instance, new FakeTimeProvider(T0));
        Assert.Equal("20260410", await v.DiscoverAsync());

        // Second call: upstream broken
        handler.Handler = _ => throw new HttpRequestException("boom");
        Assert.Null(await v.DiscoverAsync());
        Assert.Equal("20260410", v.CurrentDate);
    }

    [Fact]
    public async Task Override_ShortCircuitsDiscovery()
    {
        var handler = new MockHttpMessageHandler();
        handler.Handler = _ => throw new InvalidOperationException("should not be called");
        var http = new HttpClient(handler);
        var v = new VfrmapCycle(
            http, NullLogger<VfrmapCycle>.Instance, new FakeTimeProvider(T0),
            overrideDate: "20260101");
        Assert.Equal("20260101", v.CurrentDate);
        Assert.Equal("20260101", await v.DiscoverAsync());
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CachePersistsAndReloads()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"flightjar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var path = Path.Combine(tmp, "vfrmap_cycle.json");
        try
        {
            var handler = new MockHttpMessageHandler();
            handler.Handler = req => req.RequestUri!.AbsoluteUri == "https://vfrmap.com/"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""<script src="js/map.js"></script>"""),
                }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("var f='20260410';") };
            var http = new HttpClient(handler);
            var v1 = new VfrmapCycle(http, NullLogger<VfrmapCycle>.Instance, new FakeTimeProvider(T0), cachePath: path);
            await v1.DiscoverAsync();

            // New instance, cache-only load, no network.
            var handler2 = new MockHttpMessageHandler();
            handler2.Handler = _ => throw new InvalidOperationException("no network expected");
            var v2 = new VfrmapCycle(new HttpClient(handler2), NullLogger<VfrmapCycle>.Instance, new FakeTimeProvider(T0), cachePath: path);
            await v2.LoadCacheAsync();
            Assert.Equal("20260410", v2.CurrentDate);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
