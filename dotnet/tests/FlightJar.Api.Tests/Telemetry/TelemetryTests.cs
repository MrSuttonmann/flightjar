using System.Net;
using System.Text.Json;
using FlightJar.Api.Telemetry;
using FlightJar.Core.Configuration;

namespace FlightJar.Api.Tests.Telemetry;

public class InstanceIdStoreTests
{
    [Fact]
    public async Task FirstRun_GeneratesAndPersistsId()
    {
        var path = NewTempFile();
        try
        {
            var store = new InstanceIdStore(path);
            await store.LoadOrCreateAsync();

            Assert.False(string.IsNullOrWhiteSpace(store.InstanceId));
            Assert.True(File.Exists(path));

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var saved = doc.RootElement.GetProperty("instance_id").GetString();
            Assert.Equal(store.InstanceId, saved);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SecondLoad_PreservesId()
    {
        var path = NewTempFile();
        try
        {
            var first = new InstanceIdStore(path);
            await first.LoadOrCreateAsync();
            var firstId = first.InstanceId;

            var second = new InstanceIdStore(path);
            await second.LoadOrCreateAsync();

            Assert.Equal(firstId, second.InstanceId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task NoPath_StillProducesAnId()
    {
        var store = new InstanceIdStore(path: null);
        await store.LoadOrCreateAsync();
        Assert.False(string.IsNullOrWhiteSpace(store.InstanceId));
    }

    [Fact]
    public async Task Reset_GeneratesDifferentIdAndRefreshesFirstSeen()
    {
        var path = NewTempFile();
        try
        {
            var store = new InstanceIdStore(path);
            await store.LoadOrCreateAsync();
            var oldId = store.InstanceId;
            var oldFirstSeen = store.FirstSeen;

            // Sleep just enough to make first_seen demonstrably move
            // forward. ResetAsync uses the wall clock.
            await Task.Delay(5);
            await store.ResetAsync();

            Assert.NotEqual(oldId, store.InstanceId);
            Assert.True(store.FirstSeen >= oldFirstSeen);

            // The persisted file should now reflect the rotated id.
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(store.InstanceId, doc.RootElement.GetProperty("instance_id").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Reset_NoPath_StillRotatesInMemory()
    {
        var store = new InstanceIdStore(path: null);
        await store.LoadOrCreateAsync();
        var oldId = store.InstanceId;

        await store.ResetAsync();

        Assert.NotEqual(oldId, store.InstanceId);
    }

    [Fact]
    public async Task CorruptFile_RegeneratesId()
    {
        var path = NewTempFile();
        try
        {
            File.WriteAllText(path, "{not json");

            var store = new InstanceIdStore(path);
            await store.LoadOrCreateAsync();

            Assert.False(string.IsNullOrWhiteSpace(store.InstanceId));
            // File should now be valid JSON with the regenerated id.
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(store.InstanceId, doc.RootElement.GetProperty("instance_id").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string NewTempFile() => Path.Combine(
        Path.GetTempPath(), $"fj-telemetry-test-{Guid.NewGuid():N}.json");
}

public class TelemetryPayloadBuilderTests
{
    private static TelemetryInputs MakeInputs(
        int aircraftCount = 0,
        int positioned = 0,
        int ws = 0,
        int watchlist = 0,
        IReadOnlyList<string>? channelTypes = null,
        long framesSinceLastPing = 0,
        double secondsSinceLastPing = 0,
        TelemetryAccumulator.Snapshot? acc = null,
        double polarMaxKm = 0,
        double polarP95Km = 0,
        int? busiestHour = null,
        long rssBytes = 0,
        bool aircraftDbOverridden = false) =>
        new()
        {
            AircraftCount = aircraftCount,
            AircraftPositioned = positioned,
            WsSubscribers = ws,
            WatchlistSize = watchlist,
            EnabledNotificationChannelTypes = channelTypes ?? Array.Empty<string>(),
            FramesSinceLastPing = framesSinceLastPing,
            SecondsSinceLastPing = secondsSinceLastPing,
            Accumulator = acc ?? new TelemetryAccumulator.Snapshot(0, 0, 0, 0, 0, 0, 0, 0),
            PolarCoverageMaxKm = polarMaxKm,
            PolarCoverageP95Km = polarP95Km,
            HeatmapBusiestHourUtc = busiestHour,
            ProcessRssBytes = rssBytes,
            AircraftDbOverridden = aircraftDbOverridden,
        };

    [Fact]
    public void Payload_IncludesCoreFields()
    {
        var opts = new AppOptions { LatRef = 52.98, LonRef = -1.20 };
        var firstSeen = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var startedAt = DateTimeOffset.Parse("2026-04-24T12:00:00Z");
        var now = startedAt.AddSeconds(3600);

        var props = TelemetryPayloadBuilder.Build(opts, firstSeen, startedAt, now,
            MakeInputs(aircraftCount: 42, positioned: 38, ws: 2,
                       channelTypes: new[] { "telegram" }));

        Assert.Equal("flightjar", props["$lib"]);
        Assert.Equal(3600L, props["uptime_s"]);
        Assert.Equal(42, props["aircraft_count"]);
        Assert.Equal(38, props["aircraft_positioned"]);
        Assert.Equal(2, props["ws_subscribers"]);
        Assert.Equal(1, props["feature_notification_channels"]);
        Assert.Equal(firstSeen.ToString("O"), props["first_seen_iso"]);
    }

    [Fact]
    public void Region_RoundsToNearest10Degrees()
    {
        var opts = new AppOptions { LatRef = 52.98, LonRef = -1.20, BlackspotsEnabled = true };
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MakeInputs());

        // 52.98 → 50, -1.20 → 0
        Assert.Equal(50, props["region_lat_10"]);
        Assert.Equal(0, props["region_lon_10"]);
    }

    [Fact]
    public void Region_OmittedWhenReceiverUnconfigured()
    {
        var opts = new AppOptions { LatRef = null, LonRef = null };
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MakeInputs());

        Assert.False(props.ContainsKey("region_lat_10"));
        Assert.False(props.ContainsKey("region_lon_10"));
    }

    [Fact]
    public void FeatureFlags_ReflectAppOptions()
    {
        var opts = new AppOptions
        {
            LatRef = 52.98,
            LonRef = -1.20,
            FlightRoutesEnabled = true,
            MetarEnabled = false,
            OpenAipApiKey = "abc123",
            BlackspotsEnabled = true,
            Password = "secret",
        };

        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MakeInputs());

        Assert.Equal(true, props["feature_flight_routes"]);
        Assert.Equal(false, props["feature_metar"]);
        Assert.Equal(true, props["feature_openaip"]);
        Assert.Equal(true, props["feature_blackspots"]);
        Assert.Equal(true, props["feature_password_set"]);
    }

    [Fact]
    public void Blackspots_DisabledWhenReceiverUnset()
    {
        // Even if the env flag is on, the worker can't run without LAT_REF/LON_REF.
        var opts = new AppOptions { BlackspotsEnabled = true, LatRef = null, LonRef = null };
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MakeInputs());

        Assert.Equal(false, props["feature_blackspots"]);
        // Tuning knobs are omitted when the feature can't run.
        Assert.False(props.ContainsKey("blackspots_radius_km"));
        Assert.False(props.ContainsKey("blackspots_grid_deg"));
        Assert.False(props.ContainsKey("blackspots_max_agl_m"));
    }

    [Fact]
    public void Openaip_FalseOnEmptyKey()
    {
        var opts = new AppOptions { OpenAipApiKey = "" };
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MakeInputs());
        Assert.Equal(false, props["feature_openaip"]);
    }

    [Fact]
    public void Antenna_SendsExactMetres_PreferringMslOverAgl()
    {
        var opts = new AppOptions
        {
            LatRef = 52.98,
            LonRef = -1.20,
            BlackspotsAntennaAglM = 5.0,
            BlackspotsAntennaMslM = 137.4,
        };
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MakeInputs());

        Assert.Equal(137, props["antenna_msl_m"]);
        Assert.Equal("msl", props["antenna_source"]);
        Assert.False(props.ContainsKey("antenna_agl_m"));
    }

    [Fact]
    public void Antenna_FallsBackToAglWhenMslUnset()
    {
        var opts = new AppOptions { BlackspotsAntennaAglM = 7.5, BlackspotsAntennaMslM = null };
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MakeInputs());

        Assert.Equal(8, props["antenna_agl_m"]);  // round half-to-even gives 8 from 7.5
        Assert.Equal("agl", props["antenna_source"]);
        Assert.False(props.ContainsKey("antenna_msl_m"));
    }

    [Fact]
    public void BlackspotsTuning_PresentWhenFeatureCanRun()
    {
        var opts = new AppOptions
        {
            LatRef = 52.98,
            LonRef = -1.20,
            BlackspotsEnabled = true,
            BlackspotsRadiusKm = 250,
            BlackspotsGridDeg = 0.025,
            BlackspotsMaxAglM = 200,
        };
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MakeInputs());

        Assert.Equal(250.0, props["blackspots_radius_km"]);
        Assert.Equal(0.025, props["blackspots_grid_deg"]);
        Assert.Equal(200.0, props["blackspots_max_agl_m"]);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1-10")]
    [InlineData(10, "1-10")]
    [InlineData(11, "11-50")]
    [InlineData(50, "11-50")]
    [InlineData(51, "51-200")]
    [InlineData(200, "51-200")]
    [InlineData(201, "200+")]
    [InlineData(5000, "200+")]
    public void WatchlistSize_BucketsAtBoundaries(int size, string expectedBucket)
    {
        var opts = new AppOptions();
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            MakeInputs(watchlist: size));
        Assert.Equal(expectedBucket, props["watchlist_size_bucket"]);
    }

    [Fact]
    public void NotificationChannelTypes_PreservedFromInputs()
    {
        var opts = new AppOptions();
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            MakeInputs(channelTypes: new[] { "ntfy", "telegram", "webhook" }));

        var types = Assert.IsAssignableFrom<IReadOnlyList<string>>(props["notification_channel_types"]);
        Assert.Equal(new[] { "ntfy", "telegram", "webhook" }, types);
        Assert.Equal(3, props["feature_notification_channels"]);
    }

    [Fact]
    public void Rss_BucketedTo50MbSteps()
    {
        var opts = new AppOptions();
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            MakeInputs(rssBytes: 273L * 1_048_576L));  // 273 MB → 250 MB bucket
        Assert.Equal(250L, props["process_rss_mb_bucket"]);
    }

    [Fact]
    public void AccumulatorAggregates_SurfaceInPayload()
    {
        var opts = new AppOptions();
        var acc = new TelemetryAccumulator.Snapshot(
            AircraftAvg: 12.345,
            AircraftMax: 50,
            CommBAvg: 3.21,
            CommBMax: 8,
            TickAvgMs: 4.7,
            TickMaxMs: 22.0,
            Reconnects: 2,
            Samples: 86_400);

        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            MakeInputs(acc: acc));

        Assert.Equal(12.3, props["aircraft_avg"]);
        Assert.Equal(50, props["aircraft_max"]);
        Assert.Equal(3.2, props["commb_aircraft_avg"]);
        Assert.Equal(8, props["commb_aircraft_max"]);
        Assert.Equal(4.7, props["snapshot_tick_avg_ms"]);
        Assert.Equal(22.0, props["snapshot_tick_max_ms"]);
        Assert.Equal(2, props["beast_reconnects"]);
        Assert.Equal(86_400L, props["accumulator_samples"]);
    }

    [Fact]
    public void BeastFramesPerSecond_DerivedFromDelta()
    {
        var opts = new AppOptions();
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            MakeInputs(framesSinceLastPing: 8_640_000, secondsSinceLastPing: 86_400));

        // 100 frames per second.
        Assert.Equal(100.0, props["beast_frames_per_s"]);
    }

    [Fact]
    public void BeastFramesPerSecond_OmittedWhenWindowZero()
    {
        var opts = new AppOptions();
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            MakeInputs(framesSinceLastPing: 0, secondsSinceLastPing: 0));
        Assert.False(props.ContainsKey("beast_frames_per_s"));
    }

    [Fact]
    public void PolarCoverage_OmittedWhenNoBuckets()
    {
        var opts = new AppOptions();
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            MakeInputs(polarMaxKm: 0, polarP95Km: 0));
        Assert.False(props.ContainsKey("polar_coverage_max_km"));
        Assert.False(props.ContainsKey("polar_coverage_p95_km"));
    }

    [Fact]
    public void PolarCoverage_RoundedToOneDecimal()
    {
        var opts = new AppOptions();
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            MakeInputs(polarMaxKm: 423.567, polarP95Km: 312.111));
        Assert.Equal(423.6, props["polar_coverage_max_km"]);
        Assert.Equal(312.1, props["polar_coverage_p95_km"]);
    }

    [Fact]
    public void HeatmapBusiestHour_OmittedWhenNull()
    {
        var opts = new AppOptions();
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            MakeInputs(busiestHour: null));
        Assert.False(props.ContainsKey("heatmap_busiest_hour_utc"));
    }

    [Fact]
    public void HeatmapBusiestHour_PassedThrough()
    {
        var opts = new AppOptions();
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            MakeInputs(busiestHour: 14));
        Assert.Equal(14, props["heatmap_busiest_hour_utc"]);
    }

    [Fact]
    public void InstallShape_IncludesOsAndCpuFields()
    {
        var opts = new AppOptions();
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MakeInputs());

        Assert.Contains("os_platform", props.Keys);
        Assert.Contains("os_arch", props.Keys);
        Assert.Contains("cpu_count", props.Keys);
        Assert.Contains("runtime_version", props.Keys);
        Assert.Contains("container", props.Keys);
        // cpu_count must reflect the runtime, never zero.
        Assert.Equal(Environment.ProcessorCount, props["cpu_count"]);
        Assert.IsType<bool>(props["container"]);
    }

    [Fact]
    public void Identify_SplitsStableAndOnceAttributes()
    {
        var opts = new AppOptions
        {
            LatRef = 52.98,
            LonRef = -1.20,
            FlightRoutesEnabled = true,
            MetarEnabled = false,
            OpenAipApiKey = "abc",
            BlackspotsEnabled = true,
        };
        var firstSeen = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var (set, setOnce) = TelemetryPayloadBuilder.BuildIdentify(opts, firstSeen,
            MakeInputs(channelTypes: new[] { "ntfy" }, watchlist: 5));

        // first_seen lives in $set_once so PostHog won't overwrite the
        // original on subsequent identifies.
        Assert.Equal(firstSeen.ToString("O"), setOnce["first_seen_iso"]);
        Assert.False(set.ContainsKey("first_seen_iso"));

        // Stable per-install attributes go in $set.
        Assert.Equal("flightjar", set["$lib"]);
        Assert.Equal(true, set["feature_flight_routes"]);
        Assert.Equal(false, set["feature_metar"]);
        Assert.Equal(true, set["feature_openaip"]);
        Assert.Equal(true, set["feature_blackspots"]);
        Assert.Equal(50, set["region_lat_10"]);
        Assert.Equal(0, set["region_lon_10"]);
        Assert.Equal("1-10", set["watchlist_size_bucket"]);
        Assert.Contains("os_platform", set.Keys);

        // Same geoip-disable hint as the per-event payload.
        Assert.Equal(true, set["$geoip_disable"]);
        Assert.Equal("", set["$ip"]);

        // Per-event-only fields must not leak into the Person profile.
        Assert.False(set.ContainsKey("uptime_s"));
        Assert.False(set.ContainsKey("aircraft_count"));
        Assert.False(set.ContainsKey("ws_subscribers"));
        Assert.False(set.ContainsKey("aircraft_avg"));
        Assert.False(set.ContainsKey("beast_frames_per_s"));
    }

    [Fact]
    public void Identify_OmitsRegion_WhenReceiverUnconfigured()
    {
        var opts = new AppOptions { LatRef = null, LonRef = null };
        var (set, _) = TelemetryPayloadBuilder.BuildIdentify(opts, DateTimeOffset.UtcNow, MakeInputs());
        Assert.False(set.ContainsKey("region_lat_10"));
        Assert.False(set.ContainsKey("region_lon_10"));
    }

    [Fact]
    public void Payload_DisablesPosthogServerSideGeoip()
    {
        // Without $geoip_disable, PostHog enriches the event from the
        // source IP and the Person profile's location flips around as
        // the user's public IP resolves to different cities. We send
        // our own coarse rounded region instead, so server-side geoip
        // must be off and the IP must be blanked.
        var opts = new AppOptions { LatRef = 52.98, LonRef = -1.20 };
        var props = TelemetryPayloadBuilder.Build(
            opts, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, MakeInputs());

        Assert.Equal(true, props["$geoip_disable"]);
        Assert.Equal("", props["$ip"]);
    }
}

public class TelemetryAccumulatorTests
{
    [Fact]
    public void DrainAndReset_ReturnsAggregates_AndZeroesState()
    {
        var acc = new TelemetryAccumulator();
        acc.RecordTickSample(aircraftCount: 10, commBAircraftCount: 2, tickDurationMs: 5.0);
        acc.RecordTickSample(aircraftCount: 30, commBAircraftCount: 6, tickDurationMs: 15.0);
        acc.RecordReconnect();
        acc.RecordReconnect();

        var snap = acc.DrainAndReset();
        Assert.Equal(20.0, snap.AircraftAvg);
        Assert.Equal(30, snap.AircraftMax);
        Assert.Equal(4.0, snap.CommBAvg);
        Assert.Equal(6, snap.CommBMax);
        Assert.Equal(10.0, snap.TickAvgMs);
        Assert.Equal(15.0, snap.TickMaxMs);
        Assert.Equal(2, snap.Reconnects);
        Assert.Equal(2L, snap.Samples);

        // Drain again — should be all zeros.
        var second = acc.DrainAndReset();
        Assert.Equal(0, second.AircraftMax);
        Assert.Equal(0, second.Reconnects);
        Assert.Equal(0L, second.Samples);
    }

    [Fact]
    public void EmptyDrain_ProducesZeroAverages_WithoutDivByZero()
    {
        var acc = new TelemetryAccumulator();
        var snap = acc.DrainAndReset();
        Assert.Equal(0.0, snap.AircraftAvg);
        Assert.Equal(0.0, snap.CommBAvg);
        Assert.Equal(0.0, snap.TickAvgMs);
        Assert.Equal(0L, snap.Samples);
    }
}

public class PosthogClientTests
{
    [Fact]
    public async Task EmptyApiKey_DoesNotMakeRequest()
    {
        var handler = new RecordingHandler();
        var client = new PosthogClient(new HttpClient(handler));

        var ok = await client.CaptureAsync(
            host: "https://example.test",
            apiKey: "",
            @event: "x",
            distinctId: "id",
            properties: new Dictionary<string, object?>(),
            timestamp: DateTimeOffset.UtcNow);

        Assert.False(ok);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Capture_PostsExpectedShapeToCaptureEndpoint()
    {
        var handler = new RecordingHandler { Response = _ => new HttpResponseMessage(HttpStatusCode.OK) };
        var client = new PosthogClient(new HttpClient(handler));

        var ok = await client.CaptureAsync(
            host: "https://eu.i.posthog.com",
            apiKey: "phc_test",
            @event: "instance_ping",
            distinctId: "abc",
            properties: new Dictionary<string, object?> { ["uptime_s"] = 60 },
            timestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"));

        Assert.True(ok);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://eu.i.posthog.com/capture/", req.Uri);

        using var doc = JsonDocument.Parse(req.Body!);
        Assert.Equal("phc_test", doc.RootElement.GetProperty("api_key").GetString());
        Assert.Equal("instance_ping", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("abc", doc.RootElement.GetProperty("distinct_id").GetString());
        Assert.Equal(60, doc.RootElement.GetProperty("properties").GetProperty("uptime_s").GetInt32());
    }

    [Fact]
    public async Task Identify_PostsSpecialEventWithSetAndSetOnce()
    {
        var handler = new RecordingHandler { Response = _ => new HttpResponseMessage(HttpStatusCode.OK) };
        var client = new PosthogClient(new HttpClient(handler));

        var ok = await client.IdentifyAsync(
            host: "https://eu.i.posthog.com",
            apiKey: "phc_test",
            distinctId: "abc",
            setProperties: new Dictionary<string, object?> { ["version"] = "1.2.3" },
            setOnceProperties: new Dictionary<string, object?> { ["first_seen_iso"] = "2026-01-01T00:00:00Z" },
            timestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"));

        Assert.True(ok);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://eu.i.posthog.com/capture/", req.Uri);

        using var doc = JsonDocument.Parse(req.Body!);
        // PostHog's special event name — anything else and PostHog
        // treats this as a regular event without updating the Person.
        Assert.Equal("$identify", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("abc", doc.RootElement.GetProperty("distinct_id").GetString());
        var props = doc.RootElement.GetProperty("properties");
        Assert.Equal("1.2.3", props.GetProperty("$set").GetProperty("version").GetString());
        Assert.Equal("2026-01-01T00:00:00Z",
            props.GetProperty("$set_once").GetProperty("first_seen_iso").GetString());
    }

    [Fact]
    public async Task Identify_OmitsSetOnce_WhenEmpty()
    {
        var handler = new RecordingHandler { Response = _ => new HttpResponseMessage(HttpStatusCode.OK) };
        var client = new PosthogClient(new HttpClient(handler));

        await client.IdentifyAsync(
            host: "https://eu.i.posthog.com",
            apiKey: "phc_test",
            distinctId: "abc",
            setProperties: new Dictionary<string, object?> { ["version"] = "1.2.3" },
            setOnceProperties: null,
            timestamp: DateTimeOffset.UtcNow);

        var req = Assert.Single(handler.Requests);
        using var doc = JsonDocument.Parse(req.Body!);
        var props = doc.RootElement.GetProperty("properties");
        Assert.True(props.TryGetProperty("$set", out _));
        Assert.False(props.TryGetProperty("$set_once", out _));
    }

    [Fact]
    public async Task Identify_EmptyApiKey_DoesNotMakeRequest()
    {
        var handler = new RecordingHandler();
        var client = new PosthogClient(new HttpClient(handler));

        var ok = await client.IdentifyAsync(
            host: "https://eu.i.posthog.com",
            apiKey: "",
            distinctId: "abc",
            setProperties: new Dictionary<string, object?>(),
            setOnceProperties: null,
            timestamp: DateTimeOffset.UtcNow);

        Assert.False(ok);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NetworkFailure_ReturnsFalseInsteadOfThrowing()
    {
        var handler = new RecordingHandler { Throw = true };
        var client = new PosthogClient(new HttpClient(handler));

        var ok = await client.CaptureAsync(
            host: "https://eu.i.posthog.com",
            apiKey: "phc_test",
            @event: "x",
            distinctId: "id",
            properties: new Dictionary<string, object?>(),
            timestamp: DateTimeOffset.UtcNow);

        Assert.False(ok);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Response { get; set; }
        public bool Throw { get; set; }
        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(ct);
            }
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri?.ToString(), body));
            if (Throw) throw new HttpRequestException("simulated");
            return Response?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string? Uri, string? Body);
}
