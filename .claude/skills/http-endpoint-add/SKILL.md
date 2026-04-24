---
name: http-endpoint-add
description: Add a new HTTP endpoint to the FlightJar API — either a map/data endpoint under /api/ or an operational one like /healthz. Covers DI-via-parameter-injection, the bbox-validation pattern for spatial queries, snake_case JSON responses, the "feature disabled → return a structured payload, not 500" convention, and the `[Collection("SequentialApi")]` WebApplicationFactory tests. Use when the user asks to add a new endpoint ("add /api/X", "expose Y over HTTP", "add a reset/recompute POST for Z"). Don't use for WebSocket / SSE / streaming work.
---

# Adding an HTTP endpoint

Every HTTP endpoint in FlightJar is a minimal-API lambda in
`dotnet/src/FlightJar.Api/Program.cs`. Services are injected by
type via the lambda signature; query-string params bind by
matching parameter name. The global JSON policy is snake_case
for both property names and string enums, configured once via
`ConfigureHttpJsonOptions` — no `[JsonPropertyName]` attributes
anywhere.

Reach for **`external-client-add`** instead if the endpoint's job
is to proxy a third-party HTTP source — that skill wraps the
upstream in a typed client with caching + throttling, and the
endpoint on top becomes a one-liner.

## Touchpoints

One endpoint typically spans three files:

1. **`dotnet/src/FlightJar.Api/Program.cs`** — the `app.MapGet /
   MapPost` lambda itself, plus (if the endpoint needs a service
   not yet registered) a `builder.Services.AddSingleton<…>()`
   line up top.
2. **`dotnet/tests/FlightJar.Api.Tests/ApiEndpointsTests.cs`** —
   xUnit `[Fact]` / `[Theory]` covering at minimum: happy path,
   400 on bad input, feature-disabled response shape.
3. **`CLAUDE.md`** — one bullet under `### HTTP endpoint surface`
   describing the endpoint. Keep it to one line unless the
   semantics are load-bearing.

Add a fourth touchpoint only when the endpoint ships a new data
record: if the JSON response uses a record that didn't exist,
put it in the closest-fit namespace (`FlightJar.Core.Stats`,
`FlightJar.Persistence.Notifications`, …) rather than under
`FlightJar.Api`. Anonymous types are fine for one-off response
shapes and in fact are the norm.

## Step 1 — Pick a route path + verb

| Verb | Use for |
|---|---|
| `GET` | Reads. Snapshots, stats, reference-data queries, lookups. Must be idempotent. |
| `POST /api/X/reset` or `/recompute` | Side-effect triggers with no request body. Existing examples: `/api/coverage/reset`, `/api/blackspots/recompute`. |
| `POST /api/X` with JSON body | Replace-semantic config writes. Existing examples: `/api/notifications/config`, `/api/watchlist`. |

Paths are lowercase-snake under `/api/…`. The operational
endpoints (`/healthz`, `/metrics`) live at the root. Do not
invent a `/api/v1/…` prefix — the API is an internal contract
between the backend and `app/static/*.js`, and we version by
just shipping a matched pair.

## Step 2 — Write the lambda

Read services by declaring them as lambda parameters; the minimal
API pipeline resolves them from the DI container. Query-string
params bind by name (case-sensitive, snake_case to match the
rest of the API). Anonymous types are fine for the response;
they'll serialise snake_case automatically.

```csharp
app.MapGet("/api/your_thing", (
    YourService svc, AppOptions opts, int? limit) =>
{
    if (!opts.YourFeatureEnabled)
    {
        return Results.Json(new { enabled = false });
    }
    var n = Math.Clamp(limit ?? 50, 1, 500);
    return Results.Json(new
    {
        enabled = true,
        items = svc.Take(n),
    });
});
```

When two services share the same type (e.g. two `ILogger`
specialisations) or the parameter would otherwise look
ambiguous, tag it with `[Microsoft.AspNetCore.Mvc.FromServices]`
— see how `AirportsDb` is pulled into `/api/airports`.

### Async lambdas

Return `async` when the endpoint awaits something; the return
type should be `IResult` for consistency with the synchronous
ones. `Results.Json(…)` works for any object.

```csharp
app.MapGet("/api/flight/{callsign}", async (
    string callsign, AdsbdbClient adsbdb, CancellationToken ct) =>
{
    var info = await adsbdb.LookupRouteAsync(callsign, ct);
    return Results.Json(info is null
        ? new { callsign, origin = (string?)null, destination = (string?)null }
        : new { callsign, origin = info.Origin, destination = info.Destination });
});
```

Accept `CancellationToken ct` whenever the handler awaits — the
minimal API pipeline passes through the request-aborted token so
the client's pan-mid-fetch drops propagate down into
`HttpClient.SendAsync`.

## Step 3 — Validate inputs

Return `Results.BadRequest(new { error = "…" })` for malformed
input. The body shape `{ "error": "human-readable message" }` is
what the frontend's error toasts and Playwright assertions
expect.

### Bbox validation

For spatial endpoints taking `min_lat` / `max_lat` / `min_lon` /
`max_lon`, use the existing helper pattern rather than repeating
the check:

```csharp
static (double mnLat, double mxLat, double mnLon, double mxLon)? ReadBbox(
    double? minLat, double? maxLat, double? minLon, double? maxLon)
{
    if (minLat is not double mnLat || maxLat is not double mxLat
        || minLon is not double mnLon || maxLon is not double mxLon) return null;
    if (mnLat < -90 || mnLat > 90 || mxLat < -90 || mxLat > 90
        || mnLon < -180 || mnLon > 180 || mxLon < -180 || mxLon > 180) return null;
    return (mnLat, mxLat, mnLon, mxLon);
}
```

`ReadBbox` already exists in `Program.cs` above the OpenAIP
endpoints. Reuse it. Don't also validate "min < max" — the
antimeridian-wrap case (e.g. `min_lon=170, max_lon=-170` for
a bbox straddling the dateline) is handled by `AirportsDb.Bbox`
and friends, and rejecting it would break Pacific views.

### Cap `limit` params

Use `Math.Clamp(limit ?? DEFAULT, 1, MAX)` so a pathological
`?limit=1000000` doesn't materialise a giant response. The
existing airports / navaids endpoints clamp to 5000.

## Step 4 — Feature-disabled responses

A feature without its dependency (OPENAIP_API_KEY, LAT_REF,
BLACKSPOTS_ENABLED=1) **must not 500**. Return a structured
payload with an `enabled: false` flag plus empty collections so
the frontend can render an empty state without a console error.
This is a load-bearing convention — the tests enforce it.

```csharp
app.MapGet("/api/your_thing", (YourWorker worker) =>
{
    if (!worker.Enabled)
    {
        return Results.Json(new { enabled = false, cells = Array.Empty<object>() });
    }
    return Results.Json(new { enabled = true, cells = worker.Items });
});
```

## Step 5 — Register the service (if needed)

If the handler needs a service that isn't already in DI, add it
near the other `builder.Services.Add…` calls in the top
third of `Program.cs`. Prefer `AddSingleton` — the app is
single-process and shared state is the norm. `AddHttpClient<T>()`
registers a typed client factory (use this when the service owns
an `HttpClient`).

```csharp
builder.Services.AddSingleton<YourService>();
// or, if it needs constructor args:
builder.Services.AddSingleton(sp => new YourService(
    options.SomePath,
    sp.GetRequiredService<ILogger<YourService>>()));
```

## Step 6 — Tests

Add to `dotnet/tests/FlightJar.Api.Tests/ApiEndpointsTests.cs`.
The class is gated by `[Collection("SequentialApi")]` so tests
don't race on environment state — keep new tests inside the
existing class unless you have a reason to spin a fresh factory.
The fixture already sets BEAST_HOST to a dead address, so the
consumer is retrying in the background.

Minimum coverage:

```csharp
[Fact]
public async Task YourThing_HappyPath()
{
    var client = _factory.CreateClient();
    var resp = await client.GetAsync("/api/your_thing?limit=5");
    resp.EnsureSuccessStatusCode();
    var body = await resp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(body);
    Assert.True(doc.RootElement.TryGetProperty("items", out var items));
    Assert.Equal(JsonValueKind.Array, items.ValueKind);
}

[Theory]
[InlineData("/api/your_thing?limit=-1")]
[InlineData("/api/your_thing?limit=abc")]
public async Task YourThing_RejectsBadInput(string url)
{
    var client = _factory.CreateClient();
    var resp = await client.GetAsync(url);
    Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
}

[Fact]
public async Task YourThing_DisabledReturnsEnabledFalse()
{
    // Test fixture leaves YOUR_FEATURE_ENABLED unset.
    var client = _factory.CreateClient();
    var resp = await client.GetAsync("/api/your_thing");
    resp.EnsureSuccessStatusCode();
    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
}
```

For endpoints that exercise real BEAST frames, look at
`BeastReplayE2ETests` for the pattern — it pumps synthetic
frames into the consumer before hitting `/api/aircraft`.

## Step 7 — CLAUDE.md

Add one bullet to the `### HTTP endpoint surface` block. Match
the existing compressed style: path, one sentence of what it
returns, any notable guards. Don't duplicate the request-param
docs — curious readers go to the source.

## Step 8 — Verify

```bash
cd dotnet
dotnet format FlightJar.slnx --verify-no-changes
dotnet test tests/FlightJar.Api.Tests/
dotnet test FlightJar.slnx
```

## Do not

- Do not return 500 for a disabled feature. `enabled: false` is
  the contract — callers including the frontend and tests
  depend on it. A 500 shows up as a red error toast.
- Do not add `[JsonPropertyName]` attributes or a
  per-endpoint `JsonSerializerOptions`. The global
  `ConfigureHttpJsonOptions` + snake_case naming policy owns the
  wire format. Fighting it produces mixed casing and the
  frontend breaks in subtle ways.
- Do not construct `HttpClient` inside a handler. If you need
  upstream HTTP, add a typed client via the `external-client-add`
  skill and inject it.
- Do not do synchronous I/O inside the lambda. Reading a file,
  blocking on a task, or running CPU-heavy work stalls Kestrel's
  I/O thread. Push work into a `BackgroundService` and serve
  cached results.
- Do not log per-request at INFO unless the endpoint is truly
  rare (admin reset, test dispatch). `/api/airports` gets hit
  on every pan and a chatty log will drown real events.
- Do not put auth / rate-limiting on the API. This is a LAN
  service behind the Docker compose network; adding a
  half-implemented token check would be worse than nothing. If
  you think a real auth boundary is needed, raise it first.
