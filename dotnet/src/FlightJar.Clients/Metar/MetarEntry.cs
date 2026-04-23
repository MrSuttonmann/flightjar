using System.Text.Json;

namespace FlightJar.Clients.Metar;

/// <summary>Distilled METAR fields — only what the UI renders.</summary>
public sealed record MetarEntry
{
    public required string Raw { get; init; }
    public long? ObsTime { get; init; }
    /// <summary>Wind direction in degrees, or "VRB" for variable-wind
    /// stations. <see cref="JsonElement"/> preserves whichever token the
    /// upstream sent.</summary>
    public JsonElement? WindDir { get; init; }
    public double? WindKt { get; init; }
    public double? GustKt { get; init; }
    /// <summary>Visibility as sent by aviationweather.gov — a number
    /// (metres, non-US stations) or a string ("10+", US stations).
    /// <see cref="JsonElement"/> round-trips whichever token came in.</summary>
    public JsonElement? Visibility { get; init; }
    public double? TempC { get; init; }
    public double? DewpointC { get; init; }
    public double? AltimeterHpa { get; init; }
    /// <summary>Max-rank cloud-cover code across layers (SKC/CLR/FEW/SCT/BKN/OVC).</summary>
    public string? Cover { get; init; }
}
