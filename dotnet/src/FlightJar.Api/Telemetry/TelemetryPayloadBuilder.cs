using System.Runtime.InteropServices;
using FlightJar.Core.Configuration;

namespace FlightJar.Api.Telemetry;

/// <summary>
/// Per-ping inputs that aren't directly readable off <see cref="AppOptions"/>:
/// snapshotted counters, aggregated stats, and the worker's last-seen frame
/// counter for derivation. Pulled together by <see cref="TelemetryWorker"/>
/// from the registry, accumulator, polar coverage, watchlist, etc., and
/// handed to the builder as a single bundle.
/// </summary>
public sealed record TelemetryInputs
{
    public int AircraftCount { get; init; }
    public int AircraftPositioned { get; init; }
    public int WsSubscribers { get; init; }
    public int WatchlistSize { get; init; }
    public IReadOnlyList<string> EnabledNotificationChannelTypes { get; init; } = Array.Empty<string>();
    public long FramesSinceLastPing { get; init; }
    public double SecondsSinceLastPing { get; init; }
    public TelemetryAccumulator.Snapshot Accumulator { get; init; } =
        new(0, 0, 0, 0, 0, 0, 0, 0);
    public double PolarCoverageMaxKm { get; init; }
    public double PolarCoverageP95Km { get; init; }
    public int? HeatmapBusiestHourUtc { get; init; }
    public long ProcessRssBytes { get; init; }
    public bool AircraftDbOverridden { get; init; }
}

/// <summary>
/// Pure helper that turns the current app state into a PostHog properties
/// dictionary. Kept separate from the worker so the payload shape can be
/// asserted in unit tests without spinning up a background service.
/// </summary>
public static class TelemetryPayloadBuilder
{
    /// <summary>
    /// Build the property bag for a per-tick <c>instance_ping</c> event.
    /// Drifty / aggregate fields go here; stable per-install attributes
    /// also land on the Person profile via <see cref="BuildIdentify"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Build(
        AppOptions options,
        DateTimeOffset firstSeen,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        TelemetryInputs inputs)
    {
        var props = new Dictionary<string, object?>
        {
            ["$lib"] = "flightjar",
            ["version"] = Environment.GetEnvironmentVariable("FLIGHTJAR_VERSION") ?? "dev",
            ["uptime_s"] = (long)(now - startedAt).TotalSeconds,
            ["first_seen_iso"] = firstSeen.ToString("O"),

            // Tell PostHog not to enrich this event with $geoip_* properties
            // derived from the source IP, and clear the IP itself. Without
            // this the Person profile's "location" field gets overwritten
            // every time the public IP resolves to a different city in
            // PostHog's geoip database — producing nonsense like "your UK
            // receiver moved to Sweden". The coarse region_lat_10 /
            // region_lon_10 below are the only location data we want
            // associated with the install.
            ["$geoip_disable"] = true,
            ["$ip"] = "",
        };

        AddInstallShape(props, options, inputs);
        AddRegionAndAntenna(props, options);

        // Per-event drifty fields.
        props["aircraft_count"] = inputs.AircraftCount;
        props["aircraft_positioned"] = inputs.AircraftPositioned;
        props["ws_subscribers"] = inputs.WsSubscribers;
        props["feature_notification_channels"] = inputs.EnabledNotificationChannelTypes.Count;

        // Memory bucketed to nearest 50 MB — exact bytes uniquely fingerprint
        // a process snapshot, but the bucket tells you whether installs are
        // mostly Pi-class (≤ 200 MB) vs server-class (≥ 1 GB).
        props["process_rss_mb_bucket"] = (long)Math.Round(inputs.ProcessRssBytes / 1_048_576.0 / 50.0) * 50;

        // Rolling stats covering the window since the previous ping. All
        // come from TelemetryAccumulator, which RegistryWorker pushes
        // into on every snapshot tick.
        var acc = inputs.Accumulator;
        props["aircraft_avg"] = Round1(acc.AircraftAvg);
        props["aircraft_max"] = acc.AircraftMax;
        props["commb_aircraft_avg"] = Round1(acc.CommBAvg);
        props["commb_aircraft_max"] = acc.CommBMax;
        props["snapshot_tick_avg_ms"] = Round1(acc.TickAvgMs);
        props["snapshot_tick_max_ms"] = Round1(acc.TickMaxMs);
        props["beast_reconnects"] = acc.Reconnects;
        props["accumulator_samples"] = acc.Samples;

        // BEAST throughput inferred from the cumulative frame counter and
        // the elapsed wall-clock window between pings. First ping yields
        // a representative average since the worker startup; subsequent
        // pings reflect only the most recent 24h.
        if (inputs.SecondsSinceLastPing > 0)
        {
            props["beast_frames_per_s"] = Round1(inputs.FramesSinceLastPing / inputs.SecondsSinceLastPing);
        }

        // Coverage. p95 includes empty buckets — a lopsided pattern
        // (e.g. mountains north) reads as a lower p95 than its peak max,
        // which is exactly the diagnostic value.
        if (inputs.PolarCoverageMaxKm > 0)
        {
            props["polar_coverage_max_km"] = Round1(inputs.PolarCoverageMaxKm);
            props["polar_coverage_p95_km"] = Round1(inputs.PolarCoverageP95Km);
        }

        if (inputs.HeatmapBusiestHourUtc is int busiest)
        {
            props["heatmap_busiest_hour_utc"] = busiest;
        }

        return props;
    }

    /// <summary>
    /// Build the property pair for a PostHog <c>$identify</c> event.
    /// <c>$set</c> holds attributes that may change between runs
    /// (version, feature-flag booleans, coarse region, install shape) so
    /// the Person profile reflects current state. <c>$set_once</c> holds
    /// the install's first-seen timestamp so the original value sticks
    /// even if a later run somehow re-sends a different one.
    /// </summary>
    public static (IReadOnlyDictionary<string, object?> Set,
                   IReadOnlyDictionary<string, object?> SetOnce)
        BuildIdentify(AppOptions options, DateTimeOffset firstSeen, TelemetryInputs inputs)
    {
        var set = new Dictionary<string, object?>
        {
            ["$lib"] = "flightjar",
            ["version"] = Environment.GetEnvironmentVariable("FLIGHTJAR_VERSION") ?? "dev",

            // Same geoip-disable hint as the per-event payload — without
            // it PostHog rewrites the Person's location every time the
            // public IP resolves to a different city.
            ["$geoip_disable"] = true,
            ["$ip"] = "",
        };

        AddInstallShape(set, options, inputs);
        AddRegionAndAntenna(set, options);

        var setOnce = new Dictionary<string, object?>
        {
            ["first_seen_iso"] = firstSeen.ToString("O"),
        };

        return (set, setOnce);
    }

    /// <summary>
    /// Stable per-install attributes — feature toggles, deployment shape
    /// (OS / arch / CPU count / runtime / container), tuning knobs, and
    /// derived facts about the install (password protection, custom DB
    /// override, watchlist size bucket, notification channel mix).
    /// Goes into both the per-event and identify payloads.
    /// </summary>
    private static void AddInstallShape(
        Dictionary<string, object?> props,
        AppOptions options,
        TelemetryInputs inputs)
    {
        props["feature_flight_routes"] = options.FlightRoutesEnabled;
        props["feature_metar"] = options.MetarEnabled;
        props["feature_openaip"] = !string.IsNullOrWhiteSpace(options.OpenAipApiKey);
        props["feature_blackspots"] = options.BlackspotsEnabled
            && options.LatRef is not null && options.LonRef is not null;
        props["feature_password_set"] = !string.IsNullOrEmpty(options.Password);

        // Deployment shape — useful for knowing which platforms a runtime
        // bump would break, and for understanding the typical hardware
        // profile (Pi 4/5 = arm64 + 4 cores; NAS / server = x64 + 8+).
        props["os_platform"] = OsPlatformLabel();
        props["os_arch"] = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        props["cpu_count"] = Environment.ProcessorCount;
        props["runtime_version"] = Environment.Version.ToString();
        // /.dockerenv is the standard sentinel Docker drops into every
        // container's filesystem. Not foolproof (Podman / k8s images may
        // omit it) but a good-enough signal for "is this the recommended
        // compose deploy".
        props["container"] = File.Exists("/.dockerenv");

        // Receiver tuning knobs. Values are the user's actual config —
        // useful for spotting whether defaults need updating.
        props["snapshot_interval_s"] = options.SnapshotInterval;
        if (options.ReceiverAnonKm > 0)
        {
            props["receiver_anon_km"] = options.ReceiverAnonKm;
        }

        // Blackspots tuning knobs (only when the feature could actually run).
        if (options.BlackspotsEnabled && options.LatRef is not null && options.LonRef is not null)
        {
            props["blackspots_radius_km"] = options.BlackspotsRadiusKm;
            props["blackspots_grid_deg"] = options.BlackspotsGridDeg;
            props["blackspots_max_agl_m"] = options.BlackspotsMaxAglM;
        }

        // Distinct-but-coarse watchlist size: tells you whether a user
        // is casually watching a couple of tails or running a serious
        // operations dashboard, without revealing the actual count.
        props["watchlist_size_bucket"] = WatchlistBucket(inputs.WatchlistSize);

        // Sorted channel-type list — much higher-signal than the bare count
        // for understanding which integrations are worth investing in.
        props["notification_channel_types"] = inputs.EnabledNotificationChannelTypes;

        props["aircraft_db_overridden"] = inputs.AircraftDbOverridden;
    }

    private static void AddRegionAndAntenna(
        Dictionary<string, object?> props,
        AppOptions options)
    {
        // Coarse 10° region — enough to spot UK vs Europe vs US clustering,
        // not enough to identify a household. Skip when the receiver
        // location isn't configured.
        if (options.LatRef is double lat && options.LonRef is double lon)
        {
            props["region_lat_10"] = (int)Math.Round(lat / 10.0) * 10;
            props["region_lon_10"] = (int)Math.Round(lon / 10.0) * 10;
        }

        // Antenna height. Sent as exact integer metres — ground-level
        // vs roof vs hilltop is the diagnostic value and there are far
        // too many installs at any given altitude inside a 10° region
        // for the value alone to identify anyone.
        if (options.BlackspotsAntennaMslM is double msl)
        {
            props["antenna_msl_m"] = (int)Math.Round(msl);
            props["antenna_source"] = "msl";
        }
        else
        {
            props["antenna_agl_m"] = (int)Math.Round(options.BlackspotsAntennaAglM);
            props["antenna_source"] = "agl";
        }
    }

    private static string OsPlatformLabel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD)) return "freebsd";
        return "other";
    }

    private static string WatchlistBucket(int size) => size switch
    {
        0 => "0",
        <= 10 => "1-10",
        <= 50 => "11-50",
        <= 200 => "51-200",
        _ => "200+",
    };

    private static double Round1(double x) => Math.Round(x, 1);
}
