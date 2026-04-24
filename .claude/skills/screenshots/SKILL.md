---
name: screenshots
description: Regenerate the README screenshots (desktop + mobile) by running scripts/take_screenshots.js against a freshly-built backend and inspecting the resulting PNGs. Use when the user asks to refresh, update, or re-take the screenshots, or whenever a UI change has landed that would invalidate the current set. The script self-hosts the backend, injects a fake fleet, and mocks the /api/aircraft + /api/blackspots endpoints — no live ADS-B feed or SRTM downloads needed.
---

# Regenerating the README screenshots

`scripts/take_screenshots.js` produces the ten PNGs under `docs/screenshots/`
(`main`, `detail-panel`, `stats`, `compact`, `blackspots` × desktop + mobile).
It runs entirely offline: spawns a Kestrel instance with BEAST pointed at a
non-existent host, Playwright replaces `window.WebSocket` with a no-op shim,
then injects a fake fleet via `update_loop.js.update()` and mocks
`/api/aircraft/<icao>` + `/api/blackspots*` so the detail panel's photo and
the terrain shadow grid render without real enrichments.

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
   - `main` + `main-mobile`: 10 aircraft visible, trails coloured, sidebar populated.
   - `detail-panel` + `detail-panel-mobile`: BAW283 selected, mock plane
     photo at top, route ticket EGLL → KSFO, telemetry grid populated.
   - `stats` + `stats-mobile`: Receiver stats dialog open, uptime non-zero.
   - `compact` + `compact-mobile`: sidebar hidden, map full-width, Leaflet
     layers control collapsed (small icon top-right, not the expanded menu).
   - `blackspots` + `blackspots-mobile`: altitude slider on right edge
     (FL100 label), shaded grid cells visible around the receiver —
     yellow / orange / red / purple bands all represented.
2. If a shot regressed (layers control expanded, blackspot cells invisible,
   detail panel empty, etc.), tweak `scripts/take_screenshots.js` and re-run.
   Common knobs:
   - **Zoom / framing**: the explicit `state.map.setView(...)` calls inside
     each capture block control the view. Blackspots uses zoom 8 centred on
     the receiver; the main / detail shots rely on `update()`'s first-update
     fitBounds over the fleet.
   - **Fleet composition**: edit `buildFleet()` — each aircraft's altitude
     drives its marker + trail colour via `altColor`.
   - **Fake blackspot cells**: `mockBlackspotsGrid()`. Density + bbox affect
     how visible the shaded cells are at the captured zoom.
   - **Fake photo**: `fakePhotoDataUri()` is an inline SVG data URI so
     screenshots are fully offline-reproducible.

## README tables

The PNGs are wired into two pairs of tables in `README.md` just under the
lede: desktop then mobile, each row containing (Overview, Detail panel,
Terrain blackspots) and (Stats dialog, Compact mode). If you add a new
capture scene to the script, add it to both tables — keep desktop and
mobile parallel so screenshots read left-to-right in the same order.

## Do not

- Do not commit the `/tmp/flightjar.log` / `/tmp/flightjar.pid` files.
- Do not leave a backend running after you finish — `kill` it and
  double-check with `lsof -i :8766` if you're unsure.
- Do not hand-edit the PNGs; always regenerate from the script so the
  next person with a UI change can reproduce them.
