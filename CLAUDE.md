# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Single-process service that connects to a BEAST TCP feed (readsb /
dump1090-fa, typically port 30005), decodes Mode S / ADS-B messages,
and exposes: a Leaflet map at `/`, a floating detail panel for
per-aircraft info, HTTP API, `/ws` WebSocket pushing 1 Hz snapshots,
and (optional) per-message JSONL logging to `/data/beast.jsonl`.
Snapshots are enriched from several free public sources:

- Routes (callsign → airports) and per-tail details (registration, type,
  operator, photo URL) from [adsbdb.com](https://www.adsbdb.com/).
- Aircraft photos from [planespotters.net](https://www.planespotters.net/)
  (with adsbdb's airport-data.com URL as fallback).
- Airline IATA code + alliance from
  [OpenFlights](https://openflights.org/data.html)'s `airlines.dat`.
- Airport names + coordinates from [OurAirports](https://ourairports.com/).
- METAR weather at origin / destination from
  [aviationweather.gov](https://aviationweather.gov/).

A server-side watchlist + notification fan-out (Telegram / ntfy / generic
webhook) fires alerts on watched-tail reappearance and emergency squawks
(7500 / 7600 / 7700), whether a browser tab is open or not. Channels are
UI-managed, persisted to `/data/notifications.json`.

## Repository layout

```
dotnet/                 # Backend solution
  src/
    FlightJar.Api/              # Minimal API, DI, BackgroundServices, HTTP + WS endpoints
    FlightJar.Core/             # Registry, state, enrichment, phase/route logic,
                                # reference-data loaders, stats (polar coverage, traffic heatmap)
    FlightJar.Decoder/          # BEAST framing + Mode S + CPR (no framework deps)
    FlightJar.Clients/          # Typed HTTP clients: adsbdb, planespotters, metar, vfrmap, openaip
    FlightJar.Notifications/    # INotifier + Telegram/Ntfy/Webhook + dispatcher + AlertWatcher
    FlightJar.Persistence/      # Gzipped JSON state, watchlist, notifications config
    FlightJar.Terrain/          # SRTM tile store + line-of-sight solver (no framework deps)
  tests/
    FlightJar.Api.Tests/        # WebApplicationFactory integration + BEAST-replay E2E
    FlightJar.Core.Tests/
    FlightJar.Decoder.Tests/    # BEAST parser + ModeS/CPR vs pyModeS golden vectors
    FlightJar.Clients.Tests/
    FlightJar.Notifications.Tests/
    FlightJar.Persistence.Tests/
    FlightJar.Terrain.Tests/
  FlightJar.slnx              # Solution (slnx format)
  Directory.Build.props       # Shared compiler settings
  global.json                 # SDK pin
  Dockerfile                  # Multi-stage build, produces the flightjar image
app/
  static/                     # Leaflet frontend — ES modules, served verbatim
scripts/
  fetch_plane_shapes.py       # Build-time utility: generates app/static/tar1090_shapes.js
tests/
  js/                         # Frontend unit tests (`node --test`)
  e2e/                        # Playwright smoke suite against the running backend
docker-compose.yml            # Builds via dotnet/Dockerfile, context = .
.github/workflows/ci.yml      # check + e2e + publish jobs
```

## Common commands

```bash
# Build + run the container (reads docker-compose.yml for BEAST_HOST etc.)
docker compose up --build -d
docker compose logs -f flightjar
docker compose down

# Run the app directly (without Docker)
BEAST_HOST=localhost BEAST_PORT=30005 LAT_REF=52.98 LON_REF=-1.20 \
  dotnet run --project dotnet/src/FlightJar.Api --urls http://127.0.0.1:8080

# Dev loop
cd dotnet
dotnet format FlightJar.slnx --verify-no-changes
dotnet build FlightJar.slnx
dotnet test FlightJar.slnx
cd ..
node --test tests/js/         # frontend ES-module tests (Node 20+)
npx playwright test           # Playwright smoke against live backend
```

## Architecture

Single entry point: `dotnet/src/FlightJar.Api/Program.cs`. Everything —
BEAST consumer, registry worker, snapshot pusher, WebSocket broadcaster,
external clients, watchlist / notifications, alert watcher — runs inside
one process under the Generic Host. Shared state is owned by a single
writer thread (`RegistryWorker`) so the `AircraftRegistry` doesn't need
locks.

### Data flow

1. **`BeastConsumerService : BackgroundService`** opens a TCP connection
   to `BEAST_HOST:BEAST_PORT`, wraps it in a `PipeReader`, and parses
   frames with `BeastFrameReader.ParseMany` (`ParseOne` uses
   `stackalloc Span<byte>` for zero-alloc escape unescaping). Frames
   are written to a bounded `Channel<BeastFrame>` with drop-oldest
   overflow. Auto-reconnect with exponential backoff (1s → 30s).
2. **`RegistryWorker : BackgroundService`** owns the registry. Three
   concurrent tasks (frame pump, 1 Hz ticker, work-loop consumer)
   serialise through one `Channel<Work>` so all mutations happen on a
   single thread. Each tick: cleanup stale aircraft → build base
   snapshot via `registry.Snapshot(now)` → enrich (origin/destination
   from adsbdb cache, airport info, flight phase, airline + alliance,
   route plausibility gate) → publish to `CurrentSnapshot` (volatile
   ref for HTTP readers) → fan out via `SnapshotBroadcaster`.
   Additional per-tick hooks: watchlist last-seen bookkeeping, alert
   fan-out (`AlertWatcher.ObserveAsync`, fire-and-forget), and periodic
   `state.json.gz` / `coverage.json` / `heatmap.json` persistence.
3. **HTTP / WS endpoints** share the same DI-registered singletons.
   `/api/aircraft` serves the pre-serialised JSON from
   `CurrentSnapshot.Json` with zero per-request work. `/ws` accepts a
   WebSocket, subscribes via `SnapshotBroadcaster.Subscribe()`, and
   pumps payloads from its bounded channel (drop-oldest on stuck
   clients).

### HTTP endpoint surface

- `GET /` — single-page map UI (`app/static/index.html`).
- `GET /api/aircraft` — current enriched snapshot.
- `GET /api/aircraft/{icao24}` — adsbdb aircraft record + planespotters photo.
- `GET /api/flight/{callsign}` — adsbdb route lookup.
- `GET /api/airports` — bounded bbox of OurAirports entries. Validates
  lat/lon bounds; 400 on out-of-range inputs.
- `GET /api/navaids` — same shape for navaids.
- `GET /api/openaip/airspaces` / `GET /api/openaip/obstacles` /
  `GET /api/openaip/reporting_points` — bbox overlays backed by
  `OpenAipClient`. Return empty arrays (not 500) when
  `OPENAIP_API_KEY` is unset.
- `GET /api/map_config` — `openaip_api_key` + VFRMap chart cycle date.
- `GET /api/coverage` / `POST /api/coverage/reset` — polar coverage polygon.
- `GET /api/heatmap` / `POST /api/heatmap/reset` — weekday × hour traffic grid.
- `GET /api/blackspots?target_alt_m=N` / `POST /api/blackspots/recompute` —
  terrain-shadow grid + required-antenna-height per blocked cell. The
  `target_alt_m` query param is the altitude (MSL metres) the frontend
  slider drives, valid range (0, 20000]; omitting it uses the startup
  default (FL100 / 3048 m). Returns `{enabled:false}` when
  `BLACKSPOTS_ENABLED=0` or `LAT_REF`/`LON_REF` not set;
  `{enabled:true, computing:true}` while the initial preload is running.
  Driven by `BlackspotsWorker`, which keeps an LRU memory cache keyed on
  altitude and persists only the default-altitude grid to disk.
- `GET /api/watchlist` / `POST /api/watchlist` — server-side watchlist
  mirrored from the browser so alerts can fire with no tab open.
- `GET /api/notifications/config` / `POST /api/notifications/config` /
  `POST /api/notifications/test/{channel_id}` — CRUD + test for the
  UI-managed notification channels.
- `GET /api/stats`, `GET /healthz`, `GET /metrics` — observability.
- `WS /ws` — live snapshots, one per `SnapshotInterval`.

### Static file serving

`Program.cs` wires `UseStaticFiles` at `/static` with
`Cache-Control: no-cache` set in `OnPrepareResponse`. Browsers keep
the cached copy but revalidate via ETag on every request (304 when
unchanged, full re-fetch on change) — essential because `app.js`'s
ES-module imports don't carry content-hashed `?v=` query strings.

Static root discovery walks three tiers:
`FLIGHTJAR_STATIC_DIR` (env override) → `<AppContext.BaseDirectory>/static`
(Docker image layout) → repo-root `app/static` (dev + tests).

### BEAST wire format (`dotnet/src/FlightJar.Decoder/Beast/BeastFrameReader.cs`)

Frames are `0x1A <type> <6B MLAT ts> <1B sig> <msg>`, where any `0x1A` in
the body is escaped to `0x1A 0x1A`. `ParseOne(buf)` returns
`(consumed, frame)` and uses `consumed == 0` to mean "need more data, do
not drop". Must be respected by callers — dropping on zero desyncs the
stream. On a bad type byte or an unescaped `0x1A` inside a body, the
parser resyncs forward rather than raising.

### Mode S decoder

Hand-ported from pyModeS 3.2.0 (MIT-licensed). Organised under
`FlightJar.Decoder.ModeS`:

- `ModeSBits.cs` — bit extraction + CRC-24 table.
- `HexMessage.cs` — parse hex / bytes into `(UInt128 Bits, int TotalBits)`.
- `ModeS.cs` — `Df`, `Icao`, `Typecode`, `Crc`, `CrcValid`, `Altcode`, `Idcode`.
- `AltitudeCode.cs` — 13-bit AC field (Q=1 linear, Q=0 Gillham).
- `IdentityCode.cs` — 13-bit squawk.
- `CallsignDecoder.cs` — 6-bit ICAO callsign alphabet.
- `AdsbDecoder.cs` — DF17/18 typecode dispatch → BDS 0,5 / 0,6 / 0,8 / 0,9.
- `Cpr.cs` — airborne + surface, pair + local-reference decode. `Nl`
  table derived from DO-260B §A.1.7.2; bisect lookup.
- `MessageDecoder.cs` — top-level entry, returns `DecodedMessage`.

Every decoder is validated against pyModeS golden vectors in
`FlightJar.Decoder.Tests`.

### Aircraft state

`AircraftRegistry.Ingest(hex, now, mlatTicks, signal)` dispatches by
DF: DF 17/18 → full ADS-B (callsign / position / velocity / altitude),
DF 4/20 → altitude code, DF 5/21 → squawk, DF 11 → all-call. Position
decoding is layered:

1. **Global CPR** — needs a fresh even+odd pair within 10 s. First fix
   for a brand-new aircraft waits for this pair.
2. **Local decode against the last known position** for the same aircraft.
3. **Local decode against `LAT_REF` / `LON_REF`** (the receiver
   coordinates). Setting these makes positions appear on the first
   message and is **required** for any surface-position decoding.

The 3-stage fallback lives in `AircraftRegistry.ResolveNewPosition(...)`
as a `protected virtual` method so tests can substitute deterministic
positions without crafting real wire bytes.

A "teleport guard" rejects any new fix implying a ground speed over
~500 m/s relative to the previous position. Distance budget is
`max(10 km, elapsed_s * 0.5 km/s)` — the floor handles zero-elapsed
bursts where the `elapsed * speed` term would be misleadingly tiny.

Stale aircraft are dropped after 60 s. `Snapshot()` omits aircraft
that have no lat / callsign / altitude / squawk yet, and reuses a
cached serialised trail keyed on a per-aircraft revision counter so
snapshot builds skip rebuilding identical lists for aircraft flying
in a straight line. Trails cap at 300 points (~5 min at 1 Hz).

Two pure helpers live alongside the registry:
`FlightPhase.Classify(...)` (taxi / climb / descent / cruise /
approach) and `RoutePlausibility.IsPlausible(...)` (corridor-sum +
bearing-to-destination gates for adsbdb route cross-check).

Registry exposes two callback hooks used by `RegistryWorker`:
- `OnNewAircraft(icao, ts)` → `TrafficHeatmap.Observe`.
- `OnPosition(lat, lon)` → `PolarCoverage.Observe`.

### Blackspots (terrain LOS)

Feature enabled when `BLACKSPOTS_ENABLED` is true (default) and
`LAT_REF`/`LON_REF` are set. Two moving parts:

- **`FlightJar.Terrain`** — framework-free library with three pieces:
  `SrtmTileKey` (skadi naming: `N52W002` etc.), `SrtmTile` (3601×3601
  int16 big-endian samples + bilinear interp), `SrtmTileStore` (lazy
  download from `https://elevation-tiles-prod.s3.amazonaws.com/skadi/...`,
  persists to `/data/terrain/*.hgt.gz`; empty-file sentinel for ocean
  tiles avoids 404 retries). `LineOfSightSolver.Solve(...)` walks the
  great-circle path every 100 m, applies a 4/3-Earth refraction bulge,
  and bisects on antenna MSL altitude to find the minimum that clears
  (the caller passes a `ceilingMslM` upper bound — typically
  `ground_elev + MAX_AGL`).
- **`BlackspotsWorker : BackgroundService`** + **`BlackspotsGrid`** —
  at startup, load `/data/blackspots.json.gz`; if the persisted non-
  altitude params match live `AppOptions` (receiver coords, antenna h,
  radius, grid deg, max AGL), seed the LRU cache with it; else recompute
  the default altitude (`DefaultTargetAltitudeM` = FL100). SRTM tiles +
  ground elevation are preloaded once per session and reused across
  altitudes. `GetOrComputeAsync(altM)` is the hot path for the
  `/api/blackspots` endpoint: serves from memory when the altitude has
  been computed this session, else runs `BlackspotsGrid.Compute` synchron-
  ously on a `Task.Run` thread (~1-2 s on decent hardware). Cache cap is
  8 altitudes with standard LRU eviction; only the startup default is
  persisted to disk.

### External-API clients

All live under `FlightJar.Clients.*` and share a common pattern via
`CachedLookup<TKey, TValue>` (in `FlightJar.Clients.Caching`): typed
`HttpClient` via `IHttpClientFactory`, `TimeProvider` injection
(`FakeTimeProvider` in tests), 429 `Retry-After` cooldown, per-key
in-flight dedup, gzipped-JSON on-disk cache with TTLs, stale-on-error
fallback. `UpstreamRateLimitedException` is the 429 signalling
primitive.

- **`Clients/Adsbdb/AdsbdbClient`** — two caches (route by callsign,
  aircraft by ICAO24) sharing one throttle + one on-disk file
  (schema v3). `/data/flight_routes.json.gz`.
- **`Clients/Planespotters/PlanespottersClient`** — photo-URL +
  photographer credit keyed by registration. `/data/photos.json.gz`.
- **`Clients/Metar/MetarClient`** — batched METAR lookups against
  aviationweather.gov. One request per tick covers every airport in
  the current snapshot's route set. `/data/metar.json.gz`.
- **`Clients/Vfrmap/VfrmapCycle`** — scrapes vfrmap.com's homepage
  once on startup and every 6 h to discover the current FAA 28-day
  chart cycle (the date embedded in the IFR tile URLs). Persisted to
  `/data/vfrmap_cycle.json`.
- **`Clients/OpenAip/OpenAipClient`** — airspace / obstacle /
  reporting-point bbox lookups against `api.core.openaip.net`.
  Enabled when `OPENAIP_API_KEY` is set (the same key that lights up
  the raster tile overlay). Requested bbox is snapped outward to a
  2° grid and each (kind, snapped-bbox) maps to one fetch — with
  pagination followed up to `MaxPagesPerRequest` pages. Three
  parallel caches share one throttle + one on-disk file.
  `/data/openaip.json.gz`.

Reference-data loaders live under `FlightJar.Core.ReferenceData`:

- **`AircraftDb`** — ICAO24 → registration + type. Reads tar1090-db's
  gzipped CSV into a `FrozenDictionary` for O(1) lookups.
- **`AirportsDb`** — OurAirports CSV; supports bounding-box queries
  with antimeridian-wrap handling.
- **`NavaidsDb`** — same for VOR / DME / NDB family.
- **`AirlinesDb`** — OpenFlights `airlines.dat`; callsign prefix → IATA
  + airline + alliance. Hand-curated Star / oneworld / SkyTeam dict.

### Watchlist + alerts

Four modules form the alert pipeline:

- **`FlightJar.Persistence.Watchlist.WatchlistStore`** — persists a
  set of ICAO24 codes + last-seen timestamps to `/data/watchlist.json`
  (atomic write-to-temp + rename, 30 s debounced).
- **`FlightJar.Persistence.Notifications.NotificationsConfigStore`** —
  UI-managed channel list at `/data/notifications.json`. Each entry
  has `{id, type, name, enabled, watchlist_enabled, emergency_enabled, ...}`
  plus type-specific fields (Telegram bot_token / chat_id; ntfy
  url / token; webhook url). Cleans non-allowed fields on save.
- **`FlightJar.Notifications.NotifierDispatcher`** — reads channels
  from the config store on every dispatch (no reload signal needed),
  fans an alert out to every channel that opts in to the given
  `AlertCategory` (`Watchlist` | `Emergency`). Per-channel failures
  are logged and swallowed so a dead channel can't break the others.
  Three notifier types: `TelegramNotifier` (sendMessage / sendPhoto,
  MarkdownV2-escaped), `NtfyNotifier` (header-based API), `WebhookNotifier`
  (JSON POST).
- **`FlightJar.Notifications.Alerts.AlertWatcher.ObserveAsync(snap)`** —
  per-tail cooldowns: watchlist 30 min (reappearance), emergency 5 min
  (squawks 7500 / 7600 / 7700). Passes `AlertCategory` to the
  dispatcher; per-channel opt-in lives there.

### Global JSON formatting

`ConfigureHttpJsonOptions` installs `JsonNamingPolicy.SnakeCaseLower`
for property names AND a `JsonStringEnumConverter(SnakeCaseLower)` so
HTTP responses come out snake_case (`watchlist_enabled`, `bot_token`,
`"type":"webhook"`). Nulls are omitted.

### Environment variables

Handled in `FlightJar.Api.Configuration.AppOptionsBinder` against the
`FlightJar.Core.Configuration.AppOptions` record:

`BEAST_HOST`, `BEAST_PORT`, `LAT_REF`, `LON_REF`, `RECEIVER_ANON_KM`,
`SITE_NAME`, `BEAST_OUTFILE`, `BEAST_ROTATE` (`none|hourly|daily`),
`BEAST_ROTATE_KEEP`, `BEAST_STDOUT`, `BEAST_NO_DECODE`,
`SNAPSHOT_INTERVAL`, `AIRCRAFT_DB_REFRESH_HOURS`, `FLIGHT_ROUTES`,
`METAR_WEATHER`, `OPENAIP_API_KEY`, `VFRMAP_CHART_DATE`,
`BLACKSPOTS_ENABLED`, `BLACKSPOTS_ANTENNA_AGL_M`,
`BLACKSPOTS_ANTENNA_MSL_M` (optional override; preferred when known),
`BLACKSPOTS_RADIUS_KM`, `BLACKSPOTS_GRID_DEG`,
`BLACKSPOTS_MAX_AGL_M`, `TERRAIN_CACHE_DIR`. Target altitude is UI-only
(slider on the map), not env-driven. The README has the full reference
table.

Notification channels are **not** env-driven — they're user-managed via
the Alerts dialog and persisted to `/data/notifications.json`.

### External data sources

All fetched from `raw.githubusercontent.com` by the Dockerfile's build
stage (avoid the `github.com/raw/...` redirect path — it has a lower
throughput ceiling on CI and build boxes):

- **Aircraft DB** — `wiedehopf/tar1090-db` (CC0), `aircraft_db.csv.gz`.
- **Airports DB** — `davidmegginson/ourairports-data`, `airports.csv`.
- **Navaids DB** — same repo, `navaids.csv`.
- **Airlines DB** — `jpatokal/openflights/master/data/airlines.dat` (ODbL).
- **tar1090 markers** — `wiedehopf/tar1090/master/html/markers.js`,
  transformed by `scripts/fetch_plane_shapes.py` into
  `app/static/tar1090_shapes.js`.

### Persistent state on disk (`/data/`)

| File | Owner | Purpose |
|---|---|---|
| `state.json.gz` | `StateSnapshotStore` | Registry snapshot (aircraft + trails) every 30 s and on shutdown. |
| `flight_routes.json.gz` | `AdsbdbClient` | Cached adsbdb routes + tails (v3). |
| `photos.json.gz` | `PlanespottersClient` | Cached photo URLs + credits (v1). |
| `metar.json.gz` | `MetarClient` | Cached METARs (v1). |
| `coverage.json` | `PolarCoverage` | Max range per 10° bearing bucket. |
| `heatmap.json` | `TrafficHeatmap` | 7×24 weekday/hour grid. |
| `watchlist.json` | `WatchlistStore` | Watchlist of ICAO24 hex codes + last-seen map. |
| `notifications.json` | `NotificationsConfigStore` | UI-managed alert channels. |
| `vfrmap_cycle.json` | `VfrmapCycle` | Auto-discovered FAA chart cycle date. |
| `openaip.json.gz` | `OpenAipClient` | Cached OpenAIP airspaces / obstacles / reporting points per 2° bbox tile (schema v1, 7-day TTL). |
| `blackspots.json.gz` | `BlackspotsGrid` | Precomputed terrain-shadow grid: blocked cells + required antenna height per cell (schema v1). Recomputed when receiver params change. |
| `terrain/*.hgt.gz` | `SrtmTileStore` | SRTM1 elevation tiles downloaded on demand from AWS's public `elevation-tiles-prod` bucket (skadi layout). Zero-byte file = ocean / no-data sentinel, so we don't retry. |
| `aircraft_db.csv.gz` | optional | Runtime override for the baked-in DB. |
| `airports.csv` / `navaids.csv` / `airlines.dat` | optional | Runtime overrides for those DBs. |

### Networking note

The compose file joins the external `ultrafeeder_default` network so
`BEAST_HOST: ultrafeeder` resolves to the readsb/ultrafeeder container.
If you change to a different deployment (host-mode, remote readsb), the
`networks:` block and `BEAST_HOST` must be updated together.
