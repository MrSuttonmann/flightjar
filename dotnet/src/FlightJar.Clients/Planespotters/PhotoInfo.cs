namespace FlightJar.Clients.Planespotters;

/// <summary>Photo metadata returned by planespotters for a registration.</summary>
public sealed record PhotoInfo(
    string Thumbnail,
    string? Large,
    string? Link,
    string? Photographer);
