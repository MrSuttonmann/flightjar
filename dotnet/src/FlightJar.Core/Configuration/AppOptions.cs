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
    public string VfrmapChartDate { get; init; } = "";
}
