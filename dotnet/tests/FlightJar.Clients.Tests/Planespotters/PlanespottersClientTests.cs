using System.Net;
using System.Net.Http.Json;
using FlightJar.Clients.Planespotters;
using FlightJar.Clients.Tests.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FlightJar.Clients.Tests.Planespotters;

public class PlanespottersClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 23, 12, 0, 0, TimeSpan.Zero);

    private static (PlanespottersClient Client, MockHttpMessageHandler Handler, FakeTimeProvider Time) MakeClient(
        string? cachePath = null, bool enabled = true)
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);
        var time = new FakeTimeProvider(T0) { AutoAdvanceAmount = TimeSpan.FromSeconds(2) };
        var client = new PlanespottersClient(http, NullLogger<PlanespottersClient>.Instance, time, cachePath, enabled);
        return (client, handler, time);
    }

    private static HttpResponseMessage PhotoResponse(string thumbUrl = "https://cdn/thumb.jpg")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                photos = new[]
                {
                    new
                    {
                        thumbnail = new { src = thumbUrl },
                        thumbnail_large = new { src = thumbUrl.Replace("thumb", "large") },
                        link = "https://www.planespotters.net/photo/123456",
                        photographer = "Jane Doe",
                    },
                },
            }),
        };
    }

    private static HttpResponseMessage EmptyPhotoResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { photos = Array.Empty<object>() }),
        };
    }

    [Fact]
    public async Task Disabled_SkipsUpstream()
    {
        var (c, handler, _) = MakeClient(enabled: false);
        Assert.Null(await c.LookupAsync("G-EZAN"));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Lookup_ReturnsPhotoInfo()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => PhotoResponse();
        var result = await c.LookupAsync("G-EZAN");
        Assert.NotNull(result);
        Assert.Equal("https://cdn/thumb.jpg", result!.Thumbnail);
        Assert.Equal("https://cdn/large.jpg", result.Large);
        Assert.Equal("Jane Doe", result.Photographer);
        Assert.Equal("https://www.planespotters.net/photo/123456", result.Link);
    }

    [Fact]
    public async Task Lookup_CacheHit_SkipsUpstream()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => PhotoResponse();
        await c.LookupAsync("G-EZAN");
        await c.LookupAsync("G-EZAN");
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Lookup_EmptyPhotos_ReturnsNull()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => EmptyPhotoResponse();
        Assert.Null(await c.LookupAsync("G-NONE"));
    }

    [Fact]
    public async Task Registration_IsUppercased()
    {
        var (c, handler, _) = MakeClient();
        handler.Handler = _ => PhotoResponse();
        await c.LookupAsync("g-ezan");
        await c.LookupAsync("G-EZAN");
        Assert.Equal(1, handler.CallCount);
        Assert.True(c.LookupCached("G-EZAN").Known);
    }

    [Fact]
    public async Task EmptyRegistration_ReturnsNullWithoutUpstream()
    {
        var (c, handler, _) = MakeClient();
        Assert.Null(await c.LookupAsync(""));
        Assert.Null(await c.LookupAsync("   "));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FourTwoNine_SetsCooldown()
    {
        var (c, handler, time) = MakeClient();
        handler.Handler = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.TryAddWithoutValidation("Retry-After", "45");
            return r;
        };
        await c.LookupAsync("G-EZAN");
        var diff = c.Throttle.CooldownUntil - time.GetUtcNow();
        Assert.InRange(diff.TotalSeconds, 40, 50);
    }

    [Fact]
    public async Task UpstreamError_FallsBackToStaleCache()
    {
        var (c, handler, time) = MakeClient();
        handler.Handler = _ => PhotoResponse();
        await c.LookupAsync("G-EZAN");
        time.Advance(PlanespottersClient.PositiveTtl + TimeSpan.FromSeconds(1));
        handler.Handler = _ => throw new HttpRequestException("boom");
        var result = await c.LookupAsync("G-EZAN");
        Assert.NotNull(result);
        Assert.Equal("https://cdn/thumb.jpg", result!.Thumbnail);
    }

    [Fact]
    public async Task CachePersistsAndReloads()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"flightjar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var path = Path.Combine(tmp, "photos.json.gz");
        try
        {
            var (c1, h1, _) = MakeClient(cachePath: path);
            h1.Handler = _ => PhotoResponse(thumbUrl: "https://cdn/persisted.jpg");
            await c1.LookupAsync("G-PERSIST");

            var (c2, _, _) = MakeClient(cachePath: path);
            await c2.LoadCacheAsync();
            var cached = c2.LookupCached("G-PERSIST");
            Assert.True(cached.Known);
            Assert.Equal("https://cdn/persisted.jpg", cached.Data!.Thumbnail);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
