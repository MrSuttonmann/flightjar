---
name: screenshots
description: Regenerate the README screenshots (desktop + mobile) by running scripts/take_screenshots.js against a freshly-built backend and inspecting the resulting PNGs. Use when the user asks to refresh, update, or re-take the screenshots, or whenever a UI change has landed that would invalidate the current set. The script self-hosts the backend, injects a fake fleet, fetches a real aircraft photo from Wikimedia Commons, and mocks /api/aircraft + /api/blackspots + /api/stats + /api/coverage + /api/heatmap + /api/polar_heatmap so every dialog reads like a real-world install with weeks of data.
---

# Regenerating the README screenshots

`scripts/take_screenshots.js` produces the ten PNGs under `docs/screenshots/`
(`main`, `detail-panel`, `stats`, `compact`, `blackspots` × desktop + mobile).
It runs entirely offline aside from a one-off fetch of a CC-licensed photo
from Wikimedia Commons at the start of each run: spawns a Kestrel instance
with BEAST pointed at a non-existent host, Playwright replaces
`window.WebSocket` with a no-op shim, then injects a 10-aircraft fake fleet
via `update_loop.js.update()` and mocks the endpoints the Stats dialog,
detail panel, and blackspots layer reach for.

Mocked endpoints (see the `mock*` helpers at the top of the script):

- `/api/aircraft/<hex>` → tail record with the fetched G-ZBKA 787 photo
  base64-embedded as `photo_thumbnail` / `photo_url`, plus a Creative
  Commons credit line so the screenshot attribution is honest.
- `/api/blackspots*` → dense radial grid (~few hundred cells) with three
  Gaussian "ridges" NW / NE / SE + an unreachable sprinkle at the edges
  so all four legend bands (yellow / orange / red / purple) are visible.
- `/api/stats` → 6 d 13 h uptime, 8.4 M frames, 2 WS clients, fake
  `readsb.home.lan:30005` BEAST source shown as connected.
- `/api/coverage` → 36-bucket polar coverage with an east-south-east
  lobe (~500 km max range).
- `/api/heatmap` → 7×24 traffic heatmap with morning and evening peaks
  and a weekend dip.
- `/api/polar_heatmap` → 36 × 12 reception-density grid, decaying with
  distance + brighter toward the SE lobe.

## Preferred run (backend already up)

The script takes ~30 s once the backend is warm. Boot the backend yourself
in a background shell, then point the script at it with `--base` — that way
re-runs don't pay the .NET cold-start cost each time.

```bash
# 1. One-off: build the backend so dotnet run starts quickly.
(cd dotnet && dotnet build FlightJar.slnx -c Debug)

# 2. Start the backend in the harness config. Keep the PID so you can
#    stop it cleanly after the run.
BEAST_HOST=nonexistent.invalid BEAST_PORT=1 \
  LAT_REF=51.5 LON_REF=-0.1 BEAST_OUTFILE='' \
  FLIGHT_ROUTES=0 METAR_WEATHER=0 \
  BLACKSPOTS_ENABLED=0 TELEMETRY_ENABLED=0 \
  FLIGHTJAR_STATIC_DIR=$PWD/app/static \
  dotnet dotnet/src/FlightJar.Api/bin/Debug/net10.0/FlightJar.Api.dll \
  --urls http://127.0.0.1:8766 > /tmp/flightjar.log 2>&1 &
echo $! > /tmp/flightjar.pid

# 3. Wait for readiness (usually <5 s).
until curl -sf -o /dev/null http://127.0.0.1:8766/; do sleep 0.5; done

# 4. Capture. Output lands in docs/screenshots/.
node scripts/take_screenshots.js --base http://127.0.0.1:8766

# 5. Stop the backend.
kill $(cat /tmp/flightjar.pid) 2>/dev/null
```

## All-in-one (script manages the backend)

Omit `--base` and the script spawns `dotnet run` itself. Slower (cold-build
overhead) and the `dotnet run` child may outlive the node script on
SIGTERM, so prefer the two-step flow above during iteration:

```bash
node scripts/take_screenshots.js
```

## After capture

1. **Read every PNG** — use the `Read` tool on each file under
   `docs/screenshots/` and check:
   - `main` + `main-mobile`: status strip reads `10 aircraft · 10
     positioned` (NOT "undefined aircraft"), 10 markers placed, sidebar
     rows populated with route + registration + type.
   - `detail-panel` + `detail-panel-mobile`: BAW283 selected, a real
     photograph of G-ZBKA at the top (not the fallback SVG), Creative
     Commons credit visible, route ticket EGLL → KSFO, telemetry grid.
   - `stats` + `stats-mobile`: uptime / frames / rate / WS clients all
     populated (not "—"), traffic heatmap has clear peaks, polar
     coverage reports a max range ≥ 300 nm, reception-density polar
     heatmap shows a visible lobe.
   - `compact` + `compact-mobile`: sidebar hidden, map full-width,
     Leaflet layers control **collapsed** (small icon top-right, not
     the expanded list of overlay checkboxes — that's a common
     regression on mobile).
   - `blackspots` + `blackspots-mobile`: altitude slider labelled FL100
     at the right edge, dense grid of shaded cells covering most of
     the receiver radius with yellow / orange / red / purple bands all
     represented, and one cell's tooltip popped open showing
     "Blind spot at FL100 / Needs antenna ≥ X m MSL".

2. If a shot regressed (layers control expanded, blackspot tooltip
   missing, stats empty, photo didn't load, etc.), tweak
   `scripts/take_screenshots.js` and re-run. Common knobs:
   - **Fleet**: `buildFleet()` — altitudes drive marker/trail colours.
   - **Photo**: `COMMONS_PHOTO_URL`. If Wikimedia goes down the run
     falls back to `fallbackPhotoSvg()`; swap in another Commons URL if
     you need a different aircraft.
   - **Blackspot density / shape**: `mockBlackspotsGrid()` — ridge
     parameters (`cLat`, `cLon`, `peak`, `sigma`) and the `delta < 8`
     prune threshold control density.
   - **Stats realism**: `mockStatsPayload`, `mockCoveragePayload`,
     `mockHeatmapPayload`, `mockPolarHeatmapPayload`.
   - **Framing**: the explicit `state.map.setView(...)` calls inside
     each capture block. Blackspots uses zoom 8 centred on the
     receiver; main/detail rely on `update()`'s first-tick fitBounds.

## README tables

The PNGs are wired into two pairs of tables in `README.md` just under the
lede: desktop then mobile, each row containing (Overview, Detail panel,
Terrain blackspots) and (Stats dialog, Compact mode). If you add a new
capture scene to the script, add it to both tables — keep desktop and
mobile parallel so screenshots read left-to-right in the same order.

## Do not

- Do not commit `/tmp/flightjar.log` or `/tmp/flightjar.pid`.
- Do not leave a backend running after you finish — `kill` it and
  double-check with `lsof -i :8766` if you're unsure.
- Do not hand-edit the PNGs; always regenerate from the script so the
  next person with a UI change can reproduce them.
- Do not swap the Commons photo URL for a non-CC source — the detail
  panel displays the credit line straight from the mock response, so
  the attribution has to stay truthful.
