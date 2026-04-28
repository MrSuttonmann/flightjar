using FlightJar.Clients.Adsbdb;
using FlightJar.Clients.Planespotters;

namespace FlightJar.Api.Endpoints;

internal static class AircraftEndpoints
{
    public static IEndpointRouteBuilder MapAircraftEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/flight/{callsign}", async (
            string callsign, AdsbdbClient adsbdb, CancellationToken ct) =>
        {
            var info = await adsbdb.LookupRouteAsync(callsign, ct);
            if (info is null)
            {
                return Results.Json(new
                {
                    callsign,
                    origin = (string?)null,
                    destination = (string?)null,
                    error = "no route data",
                });
            }
            return Results.Json(new
            {
                callsign,
                origin = info.Origin,
                destination = info.Destination,
            });
        });

        app.MapGet("/api/aircraft/{icao24}", async (
            string icao24,
            AdsbdbClient adsbdb,
            PlanespottersClient planespotters,
            CancellationToken ct) =>
        {
            if (AdsbdbClient.NormaliseIcao(icao24) is null)
            {
                return Results.BadRequest(new { error = "bad ICAO24" });
            }
            var record = await adsbdb.LookupAircraftAsync(icao24, ct);
            var registration = record?.Registration?.Trim();
            var photo = !string.IsNullOrEmpty(registration)
                ? await planespotters.LookupAsync(registration, ct)
                : null;
            return Results.Json(new
            {
                registration = record?.Registration,
                type = record?.Type,
                icao_type = record?.IcaoType,
                manufacturer = record?.Manufacturer,
                @operator = record?.Operator,
                operator_country = record?.OperatorCountry,
                operator_country_iso = record?.OperatorCountryIso,
                photo_url = photo?.Large ?? record?.PhotoUrl,
                photo_thumbnail = photo?.Thumbnail ?? record?.PhotoThumbnail,
                photo_link = photo?.Link,
                photo_photographer = photo?.Photographer,
            });
        });

        return app;
    }
}
