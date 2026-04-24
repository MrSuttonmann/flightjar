---
name: external-client-add
description: Add a new typed HTTP client for an external data source (like a new route-lookup API, flight database, weather provider, photo service), wired into the snapshot enrichment pipeline with the standard CachedLookup + disk cache + 429 throttle pattern. Use when the user asks to integrate a new upstream data source — e.g. "add FlightRadar24 lookups", "pull winds aloft from NOAA", "fetch runway data from OurAirports". Don't use for local reference data bundled into the image (aircraft_db.csv.gz et al); those live under FlightJar.Core/ReferenceData.
---

# Adding an external HTTP data source

Every external data source in FlightJar follows the same template:
typed `HttpClient` via `IHttpClientFactory` + `CachedLookup<TKey, TValue>`
+ gzipped on-disk JSON cache + `HttpThrottle` + per-key in-flight dedup
+ stale-on-error fallback. The shared plumbing lives in
`FlightJar.Clients/Caching/`; individual clients own their upstream URL
shape, response parsing, and TTL policy.

Good references when starting: `PlanespottersClient` (single cache +
single throttle, most minimal), `AdsbdbClient` (two caches sharing one
throttle + one disk file via schema version), `MetarClient` (batched
bulk fetch instead of per-key lookups).

## Touchpoints

1. **`dotnet/src/FlightJar.Clients/<Name>/<Name>Client.cs`** — the
   client itself. Uses `CachedLookup` for memory caching + dedup,
   `HttpThrottle` for inter-request rate limiting, `GzipJsonCache` for
   `/data/<name>.json.gz` persistence.
2. **`dotnet/src/FlightJar.Clients/<Name>/<Record>.cs`** — the POCO
   returned from lookups (e.g. `PhotoInfo`, `MetarEntry`). Plain
   C# record; system-level serializer handles naming.
3. **`dotnet/src/FlightJar.Api/Program.cs`** — register the typed
   HttpClient + singleton + add the cache-load step to startup.
4. **`dotnet/src/FlightJar.Core/State/RegistrySnapshot.cs`** — if
   the new data is surfaced per aircraft, add fields to
   `SnapshotAircraft`.
5. **`dotnet/src/FlightJar.Api/Workers/RegistryWorker.cs`** (or the
   snapshot pusher if you're in a new codebase path) — add the
   per-tick enrichment loop that reads the cache.
6. **Env var** — add a `FLIGHT_*` / `NEW_CLIENT_*` toggle to
   `FlightJar.Core/Configuration/AppOptions.cs` +
   `FlightJar.Api/Configuration/AppOptionsBinder.cs`, and wire it
   through to the client's `Enabled` flag.
7. **Tests** — `dotnet/tests/FlightJar.Clients.Tests/<Name>/...`
   with a mocked `HttpMessageHandler` under `Mocks/`.
8. **`README.md`** — add to the "What you get" bullets and the
   configuration reference table.
9. **`CLAUDE.md`** — add to the External-API clients section.

## Step 1 — Pick the caching shape

Three variants are in use. Pick whichever matches your upstream:

| Variant | Example | Use when |
|---|---|---|
| Single cache | `PlanespottersClient` | One-in-one-out per key lookup, e.g. registration → photo. |
| Two caches, one disk file | `AdsbdbClient` | Two related lookups (routes by callsign + aircraft by ICAO) sharing a throttle so concurrent misses don't blow past the rate limit. |
| Batched | `MetarClient` | Upstream supports "give me N items in one call" — one request per tick covering every key in the current snapshot's working set. |

**Do not** invent a fourth variant. If none of these fit, ask before
coding — it means the upstream has unusual shape (pagination, per-tile
fanout, multi-kind responses) that probably warrants a discussion.
`OpenAipClient` is the existing exception (bbox-tile + multi-kind);
study it before deviating further.

## Step 2 — Write the client

Minimum skeleton (single-cache variant):

```csharp
public sealed class FooClient : IAsyncDisposable
{
    public const string Url = "https://upstream.example/api/{0}";
    public const int CacheSchemaVersion = 1;

    public static readonly TimeSpan PositiveTtl = TimeSpan.FromHours(12);
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(30);
    public const int CacheMaxSize = 10_000;
    public static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(1.2);

    private readonly HttpClient _http;
    private readonly ILogger<FooClient> _logger;
    private readonly TimeProvider _time;
    private readonly string? _cachePath;
    private readonly GzipJsonCache _diskCache;
    private readonly CachedLookup<string, FooRecord> _cache;
    private readonly HttpThrottle _throttle;

    public bool Enabled { get; }

    // ... constructor sets the above fields and initialises the
    // CachedLookup with ("<name>", PositiveTtl, NegativeTtl,
    // CacheMaxSize, _throttle, _time, logger).
}
```

Always:

- Use `IHttpClientFactory`-created `HttpClient` (registered as typed
  client in Program.cs). Never `new HttpClient()`.
- Inject `TimeProvider` so tests can run against `FakeTimeProvider`.
- Use `CachedLookup.LookupAsync` — it handles in-flight dedup,
  positive/negative TTL, and stale-on-error. Do not roll your own
  `Dictionary<TKey, Task<TValue>>`.
- Translate 429 responses into `UpstreamRateLimitedException` so
  `CachedLookup` honours the `Retry-After` cooldown.
- Persist only serialisable POCOs to the on-disk cache. Version the
  schema via `CacheSchemaVersion`; when you change the shape, bump
  the version so old caches are dropped cleanly.

## Step 3 — Register in Program.cs

Follow the existing AddHttpClient + AddSingleton pattern:

```csharp
builder.Services.AddHttpClient<FooClient>();
builder.Services.AddSingleton(sp => new FooClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(FooClient)),
    sp.GetRequiredService<ILogger<FooClient>>(),
    cachePath: Path.Combine(dataDir, "foo.json.gz"),
    enabled: appOptions.FooEnabled));
```

If the client needs a cache-load at startup, wire that via
`CreateAsync` inside the `IHostedService` that kicks off workers.

## Step 4 — Snapshot enrichment

In `RegistryWorker`'s per-tick loop (`BuildEnrichedSnapshotAsync` or
the nearest equivalent), add a synchronous-at-snapshot-time lookup:

```csharp
var fooCached = fooClient.LookupCached(key);
// use cached value in the snapshot
```

Then kick off async warm-up for any cache misses so the next tick
has the data ready, bounded by the worker's per-tick budget. Look at
how `MetarClient` batches misses via `RefreshMissingAsync(keys, ct)`
for the pattern.

**Do not** call `LookupAsync` synchronously on the snapshot path —
that serialises the whole snapshot build behind the upstream's
latency. Async warm-up + cached reads is the right discipline.

## Step 5 — Env var

In `AppOptions.cs` add `public bool FooEnabled { get; init; } = true;`
and in `AppOptionsBinder.cs` bind it from an env var like
`FOO_ENABLED`. Default to enabled unless the upstream requires a
key / has questionable ToS — then default to off (see the OPENAIP
handling).

## Step 6 — Tests

Mock the `HttpMessageHandler` using the harness in
`tests/FlightJar.Clients.Tests/Mocks/`. Cover:

- Happy path (200 + parse + cache entry written).
- 404 / negative response populates the negative-TTL cache so we
  don't re-hit the upstream for the same key.
- 429 → `UpstreamRateLimitedException` → caller-visible retry
  cooldown. Use `FakeTimeProvider` to advance past the cooldown.
- Disk cache round-trip (write cache, instantiate a new client
  pointed at the same path, assert cached value returns from memory
  without HTTP traffic).

## Step 7 — README + CLAUDE.md

Add one bullet to the "What you get" list explaining what the user
sees, and one row to the configuration reference table describing
the env var. Add the persistence file to the `/data/` table in
CLAUDE.md. Do not omit these — if a user forks the repo and runs it,
the undocumented client is invisible.

## Verify

```bash
cd dotnet && dotnet format FlightJar.slnx --verify-no-changes
dotnet test tests/FlightJar.Clients.Tests/
dotnet test FlightJar.slnx  # catches enrichment regressions
```

## Do not

- Do not fetch from `github.com/<owner>/<repo>/raw/<path>`. Use
  `raw.githubusercontent.com` — the documented reason in CLAUDE.md
  is that the redirect path has a lower throughput ceiling on CI
  and will silently rate-limit builds.
- Do not call the upstream from the hot ingest path. Enrichment
  happens on the 1 Hz snapshot tick, not per BEAST frame.
- Do not store secrets in the on-disk cache. If the client needs an
  API key, it belongs in the env and only appears in request
  headers — never serialised into `/data/*.json.gz`.
- Do not add a new client if the data could come from an existing
  one. Adsbdb already exposes routes + tails + photo URLs;
  OurAirports is already bundled for airport + navaid lookups.
  Check before adding a parallel dependency.
