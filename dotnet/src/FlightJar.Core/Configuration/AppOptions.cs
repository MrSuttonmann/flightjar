namespace FlightJar.Core.Configuration;

public enum JsonlRotateMode
{
    None,
    Hourly,
    Daily,
}

public sealed record AppOptions
{
    public string BeastHost { get; init; } = "readsb";
    public int BeastPort { get; init; } = 30005;

    public double? LatRef { get; init; }
    public double? LonRef { get; init; }
    public double ReceiverAnonKm { get; init; }
    public string? SiteName { get; init; }

    /// <summary>Per-message JSONL log path. Empty (the default) disables
    /// file logging entirely — at typical urban traffic the writer
    /// produces hundreds of MB per day and can fill a small disk in
    /// hours. Set <c>BEAST_OUTFILE=/data/beast.jsonl</c> to opt in.</summary>
    public string JsonlPath { get; init; } = "";
    public JsonlRotateMode JsonlRotate { get; init; } = JsonlRotateMode.Daily;
    public int JsonlKeep { get; init; } = 14;
    public bool JsonlStdout { get; init; }
    public bool JsonlDecode { get; init; } = true;

    public double SnapshotInterval { get; init; } = 1.0;

    public double AircraftDbRefreshHours { get; init; }

    public bool FlightRoutesEnabled { get; init; } = true;
    public bool MetarEnabled { get; init; } = true;

    public string OpenAipApiKey { get; init; } = "";

    /// <summary>Radius (km) around the receiver to pre-fetch OpenAIP tiles at
    /// startup. Fills the on-disk cache so the first map pan doesn't stall
    /// on upstream HTTP + pagination. Set to 0 to disable prewarming and
    /// fetch entirely on demand.</summary>
    public double OpenAipPrefetchRadiusKm { get; init; } = 300.0;

    public string VfrmapChartDate { get; init; } = "";

    public bool BlackspotsEnabled { get; init; } = true;

    /// <summary>Antenna tip height in metres above local ground level. Used when
    /// <see cref="BlackspotsAntennaMslM"/> is not provided.</summary>
    public double BlackspotsAntennaAglM { get; init; } = 5.0;

    /// <summary>Antenna tip height in metres MSL. When set, takes precedence
    /// over <see cref="BlackspotsAntennaAglM"/> — a measured MSL value is
    /// typically more accurate than guessing at AGL + the DEM's 30 m-resolution
    /// ground estimate. Null = fall back to AGL.</summary>
    public double? BlackspotsAntennaMslM { get; init; }

    public double BlackspotsRadiusKm { get; init; } = 400.0;
    public double BlackspotsGridDeg { get; init; } = 0.05;

    /// <summary>
    /// Pixel size (in degrees) of the "blocking face" raster — the
    /// per-pixel companion to the coarser <see cref="BlackspotsGridDeg"/>
    /// shadow grid. 0.005° ≈ 555 m N–S / ~360 m E–W at 50° N — fine
    /// enough to read individual ridges, while keeping the PNG payload
    /// small enough to ship over JSON without choking the browser.
    /// At the default 400 km radius this lands at ~3 M pixels per
    /// buffer; halving it to 0.0025° quadruples that. Don't go below
    /// ~0.002° at the default radius — the encoder allocates a raw
    /// scanline buffer and the browser has to decode each PNG, so
    /// blowing past ~20 M pixels regularly stalls the slider on
    /// commodity hardware.
    /// </summary>
    public double BlackspotsFaceGridDeg { get; init; } = 0.005;

    public double BlackspotsMaxAglM { get; init; } = 100.0;
    public string TerrainCacheDir { get; init; } = "/data/terrain";

    /// <summary>How long the blackspots feature can sit idle (no
    /// <c>/api/blackspots</c> hits) before its in-memory state — SRTM
    /// tiles (~26 MB each, ~12–15 tiles for the default radius) plus
    /// the LRU grid cache — is evicted. Disk caches survive eviction,
    /// so re-engaging the layer just pays a disk read instead of a
    /// re-download. <c>0</c> disables eviction (keep everything
    /// resident).</summary>
    public double BlackspotsIdleTimeoutMinutes { get; init; } = 15.0;

    /// <summary>Anonymous usage telemetry. When enabled (and a destination
    /// is baked into the app — see TelemetryConfig), the service emits one
    /// event per interval with a stable random instance ID, version,
    /// uptime, feature-flag booleans, aggregate traffic counts, and a
    /// coarse 10°-rounded receiver region. No per-aircraft data, no exact
    /// coordinates, no API keys / tokens. Single user opt-out:
    /// <c>TELEMETRY_ENABLED=0</c>.</summary>
    public bool TelemetryEnabled { get; init; } = true;

    /// <summary>Optional shared secret. When non-empty, the notification config
    /// + watchlist endpoints require an authenticated session cookie minted by
    /// <c>POST /api/auth/login</c> with this password. Empty disables auth
    /// entirely (backwards-compatible default for self-hosted users on a LAN).
    /// Set when exposing the instance to the public internet so unauthenticated
    /// callers can't read your bot tokens or scrape your watchlist.</summary>
    public string Password { get; init; } = "";
}
