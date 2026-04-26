using System.Net;
using System.Text.Json;
using FlightJar.Api.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
            // Stop the VFRMap cycle refresher from making a real call to
            // vfrmap.com during the test run. Without this CI was racing
            // the discovery against MapConfig_ReportsLayerStatus, which
            // expects vfrmap.enabled=false: a fast network reply
            // populated VfrmapCycle.CurrentDate before the assertion ran.
            b.ConfigureServices(services =>
            {
                var refresher = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(VfrmapCycleRefresher));
                if (refresher is not null) services.Remove(refresher);
            });
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

    [Fact]
    public async Task TelemetryConfig_ReturnsDisabled_WhenNoKeyBaked()
    {
        // Test builds don't pass `-p:PosthogApiKey=...`, so TelemetryConfig.ApiKey
        // is empty. The endpoint must return {enabled:false} rather than
        // exposing an empty key — otherwise the frontend would still try to
        // load posthog-js with a blank api_key.
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/telemetry_config");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        // No leak of host / api_key / distinct_id when disabled.
        Assert.False(doc.RootElement.TryGetProperty("api_key", out _));
        Assert.False(doc.RootElement.TryGetProperty("distinct_id", out _));
    }

    [Fact]
    public async Task TelemetryReset_RotatesDistinctId()
    {
        // Auth is off in this fixture, so the gated endpoint is open. The
        // store mints a fresh id per process (no data dir wired), and reset
        // should swap it for a new one. The endpoint returns the new id —
        // we just confirm two consecutive calls produce different values.
        var client = _factory.CreateClient();
        var first = await client.PostAsync("/api/telemetry/reset", content: null);
        first.EnsureSuccessStatusCode();
        using var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstId = firstDoc.RootElement.GetProperty("distinct_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(firstId));
        // No baked-in PostHog key in test builds, so posthog is inactive
        // and the response advertises that to the frontend.
        Assert.False(firstDoc.RootElement.GetProperty("telemetry_enabled").GetBoolean());

        var second = await client.PostAsync("/api/telemetry/reset", content: null);
        second.EnsureSuccessStatusCode();
        using var secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondId = secondDoc.RootElement.GetProperty("distinct_id").GetString();
        Assert.NotEqual(firstId, secondId);
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
    public async Task MapConfig_ReportsLayerStatus_WithReasonsWhenGatesClosed()
    {
        // Test harness leaves OPENAIP_API_KEY / VFRMAP_CHART_DATE /
        // LAT_REF unset, so every gated map layer is disabled. The
        // frontend reads layer_status to render the rows as disabled
        // with a "why and how to enable" info popover.
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/map_config");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var status = doc.RootElement.GetProperty("layer_status");
        foreach (var gate in new[] { "openaip", "vfrmap", "blackspots" })
        {
            var entry = status.GetProperty(gate);
            Assert.False(entry.GetProperty("enabled").GetBoolean());
            var reason = entry.GetProperty("reason").GetString();
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
        // Reasons must name the env var the operator needs to set so the
        // popover is actually actionable.
        Assert.Contains(
            "OPENAIP_API_KEY",
            status.GetProperty("openaip").GetProperty("reason").GetString());
        Assert.Contains(
            "VFRMAP_CHART_DATE",
            status.GetProperty("vfrmap").GetProperty("reason").GetString());
        Assert.Contains(
            "LAT_REF",
            status.GetProperty("blackspots").GetProperty("reason").GetString());
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
