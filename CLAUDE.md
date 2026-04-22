# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Single-process Python service that connects to a BEAST TCP feed (readsb/dump1090-fa,
typically port 30005), decodes Mode S / ADS-B messages with pyModeS, and exposes:
a Leaflet map at `/`, a floating detail panel for per-aircraft info, HTTP API,
`/ws` WebSocket pushing snapshots, and per-message JSONL logging to
`/data/beast.jsonl`. Snapshots are enriched from several free public sources:

- Routes (callsign → airports) and per-tail details (registration, type, operator,
  photo URL) from [adsbdb.com](https://www.adsbdb.com/).
- Aircraft photos from [planespotters.net](https://www.planespotters.net/) (with
  adsbdb's airport-data.com URL as fallback).
- Airline IATA code + alliance from [OpenFlights](https://openflights.org/data.html)'s
  `airlines.dat` — baked into the image, zero runtime network.
- Airport names + coordinates from [OurAirports](https://ourairports.com/).
- METAR weather at origin / destination from
  [aviationweather.gov](https://aviationweather.gov/).

A server-side watchlist + notification fan-out (Telegram / ntfy / generic webhook)
fires alerts on watched-tail reappearance and emergency squawks (7500 / 7600 / 7700),
whether a browser tab is open or not. Channels are UI-managed, persisted to
`/data/notifications.json`.

## Common commands

```bash
# Build + run the container (reads docker-compose.yml for BEAST_HOST etc.)
docker compose up --build -d
docker compose logs -f flightjar
docker compose down

# Run the FastAPI app directly (without Docker)
pip install -r requirements.txt
BEAST_HOST=localhost BEAST_PORT=30005 LAT_REF=52.98 LON_REF=-1.20 \
  uvicorn app.main:app --host 0.0.0.0 --port 8080

# Dev loop
pip install -r requirements-dev.txt
ruff check .            # lint
ruff format .           # apply formatting
mypy                    # type-check app/
pytest                  # backend tests
node --test tests/js/   # frontend ES-module tests (Node 20+)
```

## Architecture

Single entry point: `app/main.py` (FastAPI). Everything — map UI, WebSocket
broadcaster, JSONL logger, aircraft registry, external-API clients (adsbdb,
planespotters, METAR), watchlist store, alert watcher — runs in one asyncio
event loop, so there are no locks or cross-process IPC to reason about.

### Data flow in `app/main.py`

1. `beast_consumer()` task: opens an asyncio TCP connection to `BEAST_HOST:BEAST_PORT`,
   runs `iter_frames()` from `app/beast.py`, and for every frame:
   - Feeds Mode S messages into the shared `AircraftRegistry` (`registry.ingest`).
   - Writes a raw JSONL record via `JsonlWriter` (rotating file handler +/- stdout).
   - Auto-reconnects with exponential backoff (capped at 30s).
2. `snapshot_pusher()` task: every `SNAPSHOT_INTERVAL` seconds it evicts stale
   aircraft and calls `build_snapshot()` — which runs cache-only enrichment
   (adsbdb routes + tails, OpenFlights airline lookup, METAR attach), route
   plausibility cross-check (see `aircraft.is_plausible_route`), and phase
   classification (`aircraft.flight_phase`). Uncached lookups spawn
   fire-and-forget background fetches so the next snapshot picks them up.
   The snapshot is always built (even with no WS clients) so the alert
   watcher can fire server-side notifications whether or not a browser tab
   is open. `alerts.observe(snap)` runs via `_spawn_background(...)` so a
   slow Telegram/webhook round-trip can't stall the broadcast cadence.
3. HTTP/WS endpoints share the same `registry`, `broadcaster`, `adsbdb`,
   `planespotters`, `metar`, `watchlist_store`, `notifications_config`,
   `notifier`, and `alerts` singletons — no locks, because everything runs
   on the single asyncio event loop.

### HTTP / WebSocket endpoints

- `GET /` — single-page map UI (`app/static/index.html`, rendered with
  content-hashed asset URLs so cache-busting works across deploys).
- `GET /api/aircraft` — current snapshot (same shape as the WS push).
- `GET /api/aircraft/{icao24}` — per-tail adsbdb lookup (registration, type,
  operator, country, photo URLs) with planespotters photo upgrade.
- `GET /api/flight/{callsign}` — route lookup by callsign (origin/destination).
- `GET /api/airports` — bounded bbox of OurAirports entries for the airports
  overlay. Validates lat/lon bounds; 400 on out-of-range inputs.
- `GET /api/coverage` / `POST /api/coverage/reset` — polar coverage polygon.
- `GET /api/polar_heatmap` / `POST /api/polar_heatmap/reset` — bearing × distance
  reception-density grid (shown in the Receiver stats dialog).
- `GET /api/heatmap` / `POST /api/heatmap/reset` — weekday × hour traffic grid.
- `GET /api/watchlist` / `POST /api/watchlist` — server-side watchlist mirrored
  from the browser so alerts can fire with no tab open.
- `GET /api/notifications/config` / `POST /api/notifications/config` /
  `POST /api/notifications/test/{channel_id}` — CRUD + test for the
  UI-managed notification channels.
- `GET /api/stats`, `GET /healthz`, `GET /metrics` — observability.
- `WS /ws` — live snapshots, one per `SNAPSHOT_INTERVAL`.

### Static file caching (`RevalidatingStaticFiles` in `app/main.py`)

Only `/static/app.js` and `/static/app.css` carry a content-hash query string
(`?v=…`). The ES-module submodules (`format.js`, `units.js`, `altitude.js`,
`trend.js`, `profile.js`, `geo.js`, `tooltip.js`, `watchlist.js`,
`alerts_dialog.js`) are imported from `app.js` without a version, so the
browser would otherwise apply heuristic freshness and serve stale submodules
after a deploy (causing new named imports to fail silently).
`RevalidatingStaticFiles` sends `Cache-Control: no-cache` on every static
asset — browsers keep the cached copy but revalidate via ETag each request
(304 when unchanged, full re-fetch on change).

The Dockerfile runs `esbuild --minify` over every `app/static/*.js` and
`app/static/*.css` in place after the tar1090 shapes bundle is
generated, so the runtime image ships minified assets (~35% smaller
overall) while the repo + local dev + unit tests keep working against
the unminified source.

### BEAST wire format (`app/beast.py`)

Frames are `0x1A <type> <6B MLAT ts> <1B sig> <msg>`, where any `0x1A` in the
body is escaped to `0x1A 0x1A`. `parse_one()` returns `(bytes_consumed, frame)`
and uses `bytes_consumed == 0` to mean "need more data, do not drop". The
caller loop in `iter_frames` must respect this contract — dropping on zero
will desync the stream. On a bad type byte or an unescaped `0x1A` inside a
body, the parser resyncs forward rather than raising.

### Aircraft state (`app/aircraft.py`)

`AircraftRegistry.ingest(hex_msg, now)` dispatches by DF (downlink format):
DF 17/18 → full ADS-B, DF 4/20 → altitude code, DF 5/21 → squawk, DF 11 →
all-call. Position decoding is layered:

1. **Global CPR** — needs a fresh even+odd pair within
   `POSITION_PAIR_MAX_AGE` (10s). First fix for a brand-new aircraft waits
   for this pair.
2. **Local decode against the last known position** for the same aircraft.
3. **Local decode against `LAT_REF`/`LON_REF`** (the receiver coordinates).
   Setting these makes positions appear on the first message and is
   **required** for any surface-position decoding.

A "teleport guard" rejects any new fix implying a ground speed over
~500 m/s (~1800 km/h) relative to the previous position. Distance budget
is `max(10 km, elapsed_s * 0.5 km/s)` — the floor handles zero-elapsed
bursts where the `elapsed * speed` term would be misleadingly tiny.

Stale aircraft are dropped after `AIRCRAFT_TIMEOUT` (60s). `snapshot()`
omits aircraft that have no lat/callsign/altitude/squawk yet. The snapshot
also carries ADS-B emitter `category` (1-7 mapping to Light / Small /
Large / High-vortex / Heavy / High-performance / Rotorcraft) when known.

Two pure helpers live alongside the registry:

- `flight_phase(ac, dest_info=None)` — classifies a snapshot aircraft into
  `taxi` / `climb` / `cruise` / `descent` / `approach` (or None) from
  `vrate` + `altitude` + `on_ground`, with `approach` requiring a known
  destination within ~50km. `build_snapshot` calls it after airport info
  is attached.
- `is_plausible_route(ac, origin_info, dest_info)` — cross-checks an
  adsbdb-supplied route against the aircraft's real position and track.
  Two gates: a **corridor gate** (the two-leg sum shouldn't exceed
  `max(2× direct, direct + 300 km)`) and a **bearing gate** (airborne,
  >50km from destination, track shouldn't be >135° off the bearing-to-
  destination). Drops implausibly stale routes so the UI doesn't show a
  Brussels→Frankfurt ticket on a plane heading for JFK.

Registry exposes two callback hooks used by `main.py`:

- `on_new_aircraft(icao, ts)` — fired once per brand-new tail. Wired to
  the traffic heatmap's weekday/hour bucket.
- `on_position(lat, lon)` — fired on every accepted position fix. Wired
  to the polar-coverage max-range-per-bearing tracker.

### External-API clients

All HTTP clients share one pattern: lazy `httpx.AsyncClient` initialised on
first use (keep-alive across repeat calls), `aclose()` method called on
shutdown, per-call timeout, 429 cooldown with `Retry-After` parsing, and a
gzipped-JSON on-disk cache that survives restarts. `lookup_cached_*`
returns `(known, data)` tuples so `build_snapshot` can distinguish "never
asked" from "asked and got nothing" (and skip redundant background fetches
for the latter).

- **`app/flight_routes.py`** — `AdsbdbClient`, two lookup families sharing
  one throttle and `/data/flight_routes.json.gz` (schema `v3`):
  - Routes by callsign (12h / 1h TTLs).
  - Aircraft by ICAO24 (30d / 24h TTLs — registrations rarely change).
  - Feature-gated by `FLIGHT_ROUTES` (default on).
- **`app/photos.py`** — `PlanespottersClient`, photo-URL + photographer
  credit keyed by registration. `/data/photos.json.gz` (30d / 24h).
  The browser fetches the photos directly from the CDN, so photo
  bandwidth never touches this server. Gated by `FLIGHT_ROUTES` too
  (shares the "extra-network" kill switch).
- **`app/metar.py`** — `MetarClient`, batched METAR lookups against
  aviationweather.gov (NOAA public-domain). One request per tick covering
  every airport in the current snapshot's route set. `/data/metar.json.gz`
  (10m / 5m TTLs). Feature-gated by `METAR_WEATHER` (default on).
- **`app/vfrmap_cycle.py`** — `VfrmapCycle`, scrapes vfrmap.com's
  homepage once on startup and every 6 h to discover the current FAA
  28-day chart cycle (the date embedded in the IFR tile URLs).
  Persisted to `/data/vfrmap_cycle.json` so restarts don't need
  network. Discovery failures preserve the last-known-good cycle. The
  `VFRMAP_CHART_DATE` env var pins the cycle and short-circuits
  discovery (air-gapped deployments, bug reproduction).
- **`app/airlines_db.py`** — `AirlinesDB`, static OpenFlights dataset baked
  at Docker build time (`app/airlines.dat`; runtime override at
  `/data/airlines.dat`). Looked up by the 3-char ICAO airline code (the
  callsign prefix). Attaches IATA code + alliance membership. A small
  hand-curated `ALLIANCES` dict covers the three big alliances; airlines
  not in the dict just have no alliance tag.

### Watchlist + alerts

Three modules form the alert pipeline:

- **`app/watchlist.py`** — `WatchlistStore` persists a set of ICAO24 hex
  codes to `/data/watchlist.json` (atomic write-to-temp + rename). Mirrored
  from the browser's localStorage via `GET/POST /api/watchlist`.
- **`app/notifications_config.py`** — `NotificationsConfigStore` owns the
  user-managed channel list at `/data/notifications.json`. Each entry has
  `{id, type, name, enabled, watchlist_enabled, emergency_enabled, ...}`
  plus type-specific fields. Strips non-allowed fields on save.
- **`app/notifications.py`** — `NotifierDispatcher` reads channels from the
  config store on every dispatch (no reload signal needed when the UI
  edits), and fans a single alert out to every channel that opts in to the
  given `category` ("watchlist" | "emergency"). Per-channel failures are
  logged and swallowed so a dead Discord webhook can't break Telegram
  delivery. Three channel implementations: `TelegramNotifier` (Bot API
  `sendMessage` / `sendPhoto`, MarkdownV2-escaped), `NtfyNotifier`
  (`Title` / `Priority` / `Tags` / `Click` / `Attach` headers, optional
  bearer token), `WebhookNotifier` (minimal JSON POST for bridges). One
  shared `httpx.AsyncClient` across all three.
- **`app/alerts.py`** — `AlertWatcher.observe(snap)` is the snapshot hook
  that decides *when* to fire. Two cooldowns per aircraft:
  `WATCHLIST_COOLDOWN_S` (30 min, for watched-tail reappearance) and
  `EMERGENCY_COOLDOWN_S` (5 min, tighter because critical). Passes
  `category` through to the dispatcher; per-channel opt-in lives in the
  dispatcher.

### Frontend (`app/static/`)

- `app.js` — entry point. Connects to `/ws`, manages the aircraft map, sidebar,
  floating detail panel, and most keyboard shortcuts.
- `format.js`, `units.js`, `altitude.js`, `trend.js`, `profile.js`, `geo.js`,
  `tooltip.js`, `watchlist.js`, `alerts_dialog.js` — ES-module helpers,
  each with its own matching test file under `tests/js/` so they're
  unit-testable without a browser.
- `tar1090_shapes.js` — generated at Docker build time (`scripts/fetch_plane_shapes.py`)
  from wiedehopf/tar1090's `markers.js`. Covers ~450 ICAO type codes with
  SVG silhouettes; any type not in the bundle falls back to a generic arrow.

Four `<dialog>` elements live in `index.html` and get wired up at startup:

- **Stats** — uptime, frame counter, polar coverage, traffic heatmap.
- **About** — attribution, links, version badge.
- **Watchlist** — manage starred aircraft (add by ICAO24 hex, remove,
  toggle browser notifications, see which are currently in range).
- **Alerts** — CRUD for notification channels (`alerts_dialog.js`).
  Auto-saves each edit via `POST /api/notifications/config`; a per-row
  Test button hits `/api/notifications/test/{id}` so users can verify a
  token without waiting for a live event. Sensitive fields (Telegram bot
  tokens, ntfy auth tokens) render as password inputs with a show-toggle.

The **detail panel** (replaces the old Leaflet popup) is a single DOM element
mounted inside `#detail-content`. `buildPopupContent(a, now, airports)` creates
the skeleton once per selection; `updatePopupContent(root, a, now, airports)`
mutates placeholders in place on every snapshot tick so the aircraft
photograph, route ticket, and other static sub-elements don't flicker as the
telemetry fields update each second. On desktop the panel floats over the map
(`position: absolute`, left of the sidebar edge); on mobile it overlays the
whole viewport. The "Follow selected" behavior auto-engages when the panel
opens and disengages on close, with `panToFollowed(latlng)` offsetting the
pan target so the plane sits in the centre of the unobstructed strip of map.

**Marker updates**: icons are created once per aircraft and mutated in place
via `setLatLng()`. An explicit `iconFp` guards against rebuilding the icon
DOM on track-only changes — `setIcon()` replaces the element, which breaks
Leaflet's mousedown/mouseup click pairing if a snapshot tick lands
mid-click. For rotation-only deltas, `rotateMarkerIcon()` pokes the SVG's
transform in place.

### Environment variables

Handled in `app/main.py` (via `app/config.py`) and the Dockerfile:
`BEAST_HOST`, `BEAST_PORT`, `LAT_REF`, `LON_REF`, `RECEIVER_ANON_KM`,
`SITE_NAME`, `BEAST_OUTFILE` (empty = disable file), `BEAST_ROTATE`
(`none|hourly|daily`), `BEAST_ROTATE_KEEP`, `BEAST_STDOUT`, `BEAST_NO_DECODE`,
`SNAPSHOT_INTERVAL`, `AIRCRAFT_DB_REFRESH_HOURS`, `FLIGHT_ROUTES`,
`METAR_WEATHER`, `OPENAIP_API_KEY`, `VFRMAP_CHART_DATE`. The README has
the full reference table.

Notification channels are **not** env-driven — they're user-managed via the
Alerts dialog and persisted to `/data/notifications.json`. Anything
Telegram / ntfy / webhook-related is CRUD'd through
`NotificationsConfigStore`.

### External data sources

All fetched from `raw.githubusercontent.com` (avoid the `github.com/raw/...`
redirect path — it has a lower throughput ceiling on CI and build boxes):

- **Aircraft DB** — `wiedehopf/tar1090-db` (CC0), downloaded at build time to
  `app/aircraft_db.csv.gz`. Runtime override at `/data/aircraft_db.csv.gz`.
- **Airports DB** — `davidmegginson/ourairports-data` (public domain), baked
  in at build time to `app/airports.csv`. Runtime override at `/data/airports.csv`.
- **Navaids DB** — same repo (`navaids.csv`, public domain), baked in to
  `app/navaids.csv`. Runtime override at `/data/navaids.csv`. Drives the
  Navaids map overlay (VOR / DME / NDB family).
- **Airlines DB** — `jpatokal/openflights/master/data/airlines.dat` (ODbL),
  baked in to `app/airlines.dat`. Runtime override at `/data/airlines.dat`.
- **tar1090 markers** — `wiedehopf/tar1090/master/html/markers.js`, transformed
  by `scripts/fetch_plane_shapes.py` into `app/static/tar1090_shapes.js`.

### Persistent state on disk (`/data/`)

Everything the app owns in the volume:

| File | Owner | Purpose |
|---|---|---|
| `beast.jsonl[.YYYY-MM-DD]` | `JsonlWriter` | Rotating raw message log. |
| `state.json.gz` | `app/persistence.py` | Registry snapshot (aircraft + trails) every 30s and on shutdown. |
| `flight_routes.json.gz` | `AdsbdbClient` | Cached adsbdb routes + tails (v3). |
| `photos.json.gz` | `PlanespottersClient` | Cached photo URLs + credits (v1). |
| `metar.json.gz` | `MetarClient` | Cached METARs (v1). |
| `coverage.json` | `PolarCoverage` | Max range per 10° bearing bucket. |
| `polar_heatmap.json` | `PolarHeatmap` | 36×12 bearing × 25km-band position-fix density, 7-day rolling window (per-day sub-grids). |
| `heatmap.json` | `TrafficHeatmap` | 7×24 weekday/hour fresh-aircraft grid; each slot resets when the same weekday/hour rolls around a week later. |
| `watchlist.json` | `WatchlistStore` | Server-side watchlist of ICAO24 hex codes. |
| `notifications.json` | `NotificationsConfigStore` | UI-managed alert channels. |
| `vfrmap_cycle.json` | `VfrmapCycle` | Auto-discovered FAA chart cycle date for the VFRMap IFR overlays. Refreshed every 6 h. |
| `aircraft_db.csv.gz` | optional | Runtime override for the baked-in DB. |
| `airports.csv` / `navaids.csv` / `airlines.dat` | optional | Runtime overrides for those DBs. |

### Networking note

The compose file joins the external `ultrafeeder_default` network so
`BEAST_HOST: ultrafeeder` resolves to the readsb/ultrafeeder container. If
you change to a different deployment (host-mode, remote readsb), the
`networks:` block and `BEAST_HOST` must be updated together.
