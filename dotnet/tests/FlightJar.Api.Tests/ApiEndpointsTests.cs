using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FlightJar.Api.Tests;

/// <summary>
/// End-to-end integration smoke: boots the Api host against a dead BEAST
/// endpoint, verifies HTTP endpoints serve sensible responses while the
/// consumer is retrying. Uses <see cref="WebApplicationFactory{TEntryPoint}"/>
/// — same harness as Playwright would use for a live server.
/// </summary>
[Collection("SequentialApi")]
public class ApiEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("BEAST_HOST", "127.0.0.1");
            b.UseSetting("BEAST_PORT", "1");
            Environment.SetEnvironmentVariable("BEAST_HOST", "127.0.0.1");
            Environment.SetEnvironmentVariable("BEAST_PORT", "1");
        });
    }

    [Fact]
    public async Task Healthz_ReturnsDisconnected_WhenNotConnected()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("disconnected", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ApiAircraft_ReturnsEmptySnapshot()
    {
        var client = _factory.CreateClient();
        // Give the snapshot worker a tick to populate.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var resp = await client.GetAsync("/api/aircraft");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("aircraft", out var arr));
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
    }

    [Fact]
    public async Task ApiStats_ReportsExpectedFields()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/stats");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("beast_connected").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("frames").GetInt64());
        Assert.True(doc.RootElement.TryGetProperty("version", out _));
        Assert.True(doc.RootElement.TryGetProperty("websocket_clients", out _));
        Assert.True(doc.RootElement.TryGetProperty("uptime_s", out _));
        Assert.True(doc.RootElement.TryGetProperty("beast_target", out _));
    }

    [Fact]
    public async Task Metrics_RendersPrometheusText()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/metrics");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("flightjar_beast_connected", body);
        Assert.Contains("flightjar_frames_total", body);
        Assert.Contains("flightjar_aircraft", body);
        Assert.Contains("flightjar_ws_clients", body);
    }

    [Theory]
    [InlineData("/api/openaip/airspaces")]
    [InlineData("/api/openaip/obstacles")]
    [InlineData("/api/openaip/reporting_points")]
    public async Task OpenAip_Endpoints_RejectMissingBbox(string path)
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/openaip/airspaces")]
    [InlineData("/api/openaip/obstacles")]
    [InlineData("/api/openaip/reporting_points")]
    public async Task OpenAip_Endpoints_RejectOutOfRangeBbox(string path)
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            $"{path}?min_lat=-999&max_lat=0&min_lon=0&max_lon=0");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("/api/openaip/airspaces")]
    [InlineData("/api/openaip/obstacles")]
    [InlineData("/api/openaip/reporting_points")]
    public async Task OpenAip_Endpoints_DisabledWithoutApiKey_ReturnEmptyArray(string path)
    {
        // Test harness doesn't set OPENAIP_API_KEY, so the client is disabled
        // and the endpoints must still succeed with an empty array rather
        // than 500.
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(
            $"{path}?min_lat=50&max_lat=52&min_lon=0&max_lon=2");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Blackspots_Disabled_WhenLatRefNotSet()
    {
        // Test harness leaves LAT_REF / LON_REF unset, so the feature is
        // disabled and the endpoint reports it (rather than 500-ing).
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/blackspots");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("cells").ValueKind);
    }

    [Fact]
    public async Task Blackspots_Recompute_NoopWhenDisabled()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/blackspots/recompute", content: null);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
    }
}
