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

    public string JsonlPath { get; init; } = "/data/beast.jsonl";
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
    public double BlackspotsMaxAglM { get; init; } = 100.0;
    public string TerrainCacheDir { get; init; } = "/data/terrain";
}
