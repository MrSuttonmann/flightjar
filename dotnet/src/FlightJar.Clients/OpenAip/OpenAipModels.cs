using System.Text.Json;

namespace FlightJar.Clients.OpenAip;

/// <summary>GeoJSON-style geometry passed through to the browser verbatim.
/// <c>Coordinates</c> is a <see cref="JsonElement"/> so polygon / multipolygon /
/// point shapes all round-trip without needing three typed holders.</summary>
public sealed record GeoJsonGeometry(string Type, JsonElement Coordinates);

/// <summary>Trimmed airspace record — only the fields Leaflet needs to draw
/// + label the polygon. Altitude limits normalised to feet with a datum
/// tag ("GND" | "MSL" | "FL") so the frontend can format them without
/// repeating the OpenAIP unit-enum table.</summary>
public sealed record Airspace(
    string Id,
    string? Name,
    string? Class,
    string? TypeName,
    int? LowerFt,
    string? LowerDatum,
    int? UpperFt,
    string? UpperDatum,
    GeoJsonGeometry? Geometry);

/// <summary>Trimmed obstacle record. Geometry is always a Point so we flatten
/// to lat/lon on the wire; height normalised to feet (the usual unit on
/// VFR charts).</summary>
public sealed record Obstacle(
    string Id,
    string? Name,
    string? TypeName,
    int? HeightFt,
    int? ElevationFt,
    double Lat,
    double Lon);

/// <summary>Trimmed reporting-point record. Geometry is always a Point.</summary>
public sealed record ReportingPoint(
    string Id,
    string? Name,
    bool Compulsory,
    double Lat,
    double Lon);
