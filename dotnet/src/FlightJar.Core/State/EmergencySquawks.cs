namespace FlightJar.Core.State;

public static class EmergencySquawks
{
    /// <summary>Known emergency squawk codes mapped to a human-readable label.</summary>
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["7500"] = "hijack",
        ["7600"] = "radio",
        ["7700"] = "general",
    };

    public static string? LookupLabel(string? squawk) =>
        squawk is not null && All.TryGetValue(squawk, out var label) ? label : null;
}
