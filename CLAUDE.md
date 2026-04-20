# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Single-process Python service that connects to a BEAST TCP feed (readsb/dump1090-fa,
typically port 30005), decodes Mode S / ADS-B messages with pyModeS, and exposes:
a Leaflet map at `/`, a floating detail panel for per-aircraft info, `/api/aircraft`,
`/api/stats`, a `/ws` WebSocket pushing snapshots, and per-message JSONL logging
to `/data/beast.jsonl`. Routes (callsign → airports) and per-tail details
(registration, operator, photo) are enriched from [adsbdb.com](https://www.adsbdb.com/).

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

Single entry point: `app/main.py` (FastAPI). Map UI + WebSocket snapshots +
JSONL logger + live aircraft registry + adsbdb client all run in one asyncio loop.

### Data flow in `app/main.py`

1. `beast_consumer()` task: opens an asyncio TCP connection to `BEAST_HOST:BEAST_PORT`,
   runs `iter_frames()` from `app/beast.py`, and for every frame:
   - Feeds Mode S messages into the shared `AircraftRegistry` (`registry.ingest`).
   - Writes a raw JSONL record via `JsonlWriter` (rotating file handler +/- stdout).
   - Auto-reconnects with exponential backoff (capped at 30s).
2. `snapshot_pusher()` task: every `SNAPSHOT_INTERVAL` seconds, evicts stale
   aircraft, builds a snapshot (with adsbdb cache lookups + fire-and-forget
   background fills for uncached tails/callsigns), and broadcasts the JSON to
   every connected WebSocket client.
3. HTTP/WS endpoints share the same `registry`, `broadcaster`, and `adsbdb`
   singletons — no locks, because everything runs on the single asyncio event loop.

### HTTP / WebSocket endpoints

- `GET /` — single-page map UI (`app/static/index.html`, rendered with
  content-hashed asset URLs so cache-busting works across deploys).
- `GET /api/aircraft` — current snapshot (same shape as the WS push).
- `GET /api/aircraft/{icao24}` — per-tail adsbdb lookup (registration, type,
  operator, country, photo URLs).
- `GET /api/flight/{callsign}` — route lookup by callsign (origin/destination).
- `GET /api/airports` — bounded bbox of OurAirports entries for the airports overlay.
- `GET /api/stats`, `GET /healthz`, `GET /metrics` — observability.
- `WS /ws` — live snapshots, one per `SNAPSHOT_INTERVAL`.

### Static file caching (`RevalidatingStaticFiles` in `app/main.py`)

Only `/static/app.js` and `/static/app.css` carry a content-hash query string
(`?v=…`). ES-module submodules (`format.js`, `units.js`, `altitude.js`,
`trend.js`) are imported from `app.js` without a version, so the browser would
otherwise apply heuristic freshness and serve stale submodules after a deploy
(causing new named imports to fail silently). `RevalidatingStaticFiles` sends
`Cache-Control: no-cache` on every static asset — browsers keep the cached
copy but revalidate via ETag each request (304 when unchanged, full re-fetch
on change).

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

A "teleport guard" rejects any new fix more than ~8° lat/lon from the
previous position. Stale aircraft are dropped after `AIRCRAFT_TIMEOUT` (60s).
`snapshot()` omits aircraft that have no lat/callsign/altitude/squawk yet.
The snapshot also carries ADS-B emitter `category` (1-7 mapping to Light /
Small / Large / High-vortex / Heavy / High-performance / Rotorcraft) when
known.

### adsbdb client (`app/flight_routes.py`)

`AdsbdbClient` is one object with two lookup families — routes (by callsign)
and aircraft (by ICAO24 hex) — sharing a single throttle, 429 cooldown, and
on-disk cache file (`/data/flight_routes.json.gz`, schema `v3`). Both kinds
are lazy: cache-first, never raise on upstream error (fall back to stale
cache if any), deduplicated across concurrent callers. TTLs:

- Routes: 12h positive, 1h negative.
- Aircraft: 30d positive, 24h negative (registrations + photos rarely change).

Snapshot enrichment calls `lookup_cached_*` (synchronous, no network) and
fires `lookup_*` in the background for misses, so the next snapshot picks
up the new data. Photos are surfaced as URLs pointing at airport-data.com;
the browser fetches them directly (no proxy), so photo bandwidth never
touches this server.

The feature gate is `FLIGHT_ROUTES` (default `"1"`); setting it to `"0"`
disables all outbound lookups and suppresses photos.

### Frontend (`app/static/`)

- `app.js` — entry point. Connects to `/ws`, manages the aircraft map, sidebar,
  and the floating detail panel.
- `format.js`, `units.js`, `altitude.js`, `trend.js` — pure helpers,
  unit-testable under `tests/js/` without a browser.
- `tar1090_shapes.js` — generated at Docker build time (`scripts/fetch_plane_shapes.py`)
  from wiedehopf/tar1090's `markers.js`. Covers ~450 ICAO type codes with
  SVG silhouettes; any type not in the bundle falls back to a generic arrow.

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

### Environment variables

Handled in `app/main.py` (via `app/config.py`) and the Dockerfile:
`BEAST_HOST`, `BEAST_PORT`, `LAT_REF`, `LON_REF`, `RECEIVER_ANON_KM`,
`SITE_NAME`, `BEAST_OUTFILE` (empty = disable file), `BEAST_ROTATE`
(`none|hourly|daily`), `BEAST_ROTATE_KEEP`, `BEAST_STDOUT`, `BEAST_NO_DECODE`,
`SNAPSHOT_INTERVAL`, `AIRCRAFT_DB_REFRESH_HOURS`, `FLIGHT_ROUTES`.
The README has the full reference table.

### External data sources

All fetched from `raw.githubusercontent.com` (avoid the `github.com/raw/...`
redirect path — it has a lower throughput ceiling on CI and build boxes):

- **Aircraft DB** — `wiedehopf/tar1090-db` (CC0), downloaded at build time to
  `app/aircraft_db.csv.gz`. Runtime override at `/data/aircraft_db.csv.gz`.
- **Airports DB** — `davidmegginson/ourairports-data` (public domain), baked
  in at build time to `app/airports.csv`. Runtime override at `/data/airports.csv`.
- **tar1090 markers** — `wiedehopf/tar1090/master/html/markers.js`, transformed
  by `scripts/fetch_plane_shapes.py` into `app/static/tar1090_shapes.js`.

### Networking note

The compose file joins the external `ultrafeeder_default` network so
`BEAST_HOST: ultrafeeder` resolves to the readsb/ultrafeeder container. If
you change to a different deployment (host-mode, remote readsb), the
`networks:` block and `BEAST_HOST` must be updated together.
