# Flightjar

A small web app that shows the aircraft your ADS-B receiver can see, on a
live map — with a rolling log of every message to disk for later analysis.

It reads the BEAST feed from a running readsb, dump1090, or ultrafeeder
instance — so you can point it at whatever's already decoding ADS-B on your
network and get a lightweight map, log file, and simple API on top.

| Overview | Detail panel | Stats dialog | Compact mode |
| -------- | ------------ | ------------ | ------------ |
| ![Overview](docs/screenshots/main.png) | ![Detail panel](docs/screenshots/detail-panel.png) | ![Stats dialog](docs/screenshots/stats.png) | ![Compact mode](docs/screenshots/compact.png) |

On mobile, the sidebar stacks above the map and the detail panel slides
up as a fullscreen sheet:

| Overview | Detail panel | Stats dialog | Compact mode |
| -------- | ------------ | ------------ | ------------ |
| ![Overview (mobile)](docs/screenshots/main-mobile.png) | ![Detail panel (mobile)](docs/screenshots/detail-panel-mobile.png) | ![Stats dialog (mobile)](docs/screenshots/stats-mobile.png) | ![Compact mode (mobile)](docs/screenshots/compact-mobile.png) |


## What you get

- **Live map** at `http://<host>:8080/` with per-type plane silhouettes
  sourced from the [tar1090](https://github.com/wiedehopf/tar1090) SVG
  shape set (GPL-2.0+; covers ~450 ICAO type codes, with a generic
  arrow for anything unmapped), altitude-coloured trails showing each
  aircraft's recent altitude history, and a toggleable callsign label
  on each one.
- **Detail panel** — click any aircraft and a floating card opens with
  a photo of the plane (when we have one), its registration, type,
  operator, country flag, full route with airport names, and a full
  telemetry grid (altitude, speed, heading, vertical rate, squawk,
  distance, lat/lon, age). The panel auto-follows the plane while it's
  open and closes with the × button, the `Esc` key, or a click on empty
  map. On desktop it floats over the map so the map isn't resized;
  on mobile it overlays the whole viewport.
- **Sidebar list** of currently tracked aircraft, sortable by callsign,
  altitude, distance from the receiver, or age. Each row shows the
  registration country flag, callsign, tail/type, and live telemetry.
  Hover a plane on the map to highlight its row, and vice-versa.
- **Unit switcher** — Metric, Imperial, or Nautical — applied across
  altitude, speed, vertical rate, and distance. Your choice is remembered.
- **A record of every message** written as JSON Lines to a file on disk,
  rotated daily.
- **Optional privacy** — you can fuzz the displayed receiver location so
  sharing screenshots doesn't pin your home address on a map.
- **Optional site name** shown in the header and browser tab so you can
  tell multiple installs apart at a glance.
- **Aircraft DB enrichment** — each aircraft is tagged with its
  registration and type (e.g. `G-ABCD · BOEING 737-800`), so the detail
  panel and sidebar show the actual tail number rather than just the
  ICAO hex.
- **adsbdb enrichment** — origin / destination by callsign, operator
  name, country, and an aircraft photo, all looked up from
  [adsbdb.com](https://www.adsbdb.com/) (a free community-maintained
  API; no signup required). Routes render as `EGLL → KJFK` in the
  sidebar and as a full ticket (with airport names) in the detail panel.
  Lookups are cached server-side for 12h (routes) / 30 days (tails).
- **Flight progress + ETA** — when a flight has a known route, the detail
  panel shows a great-circle progress bar between origin and destination
  along with a live ETA at the current groundspeed. Hidden automatically
  when the aircraft is on the ground or too slow for the number to be
  meaningful.
- **At-a-glance flight state** — each aircraft is auto-classified into
  `taxi` / `climb` / `cruise` / `descent` / `approach` based on its
  vertical rate, altitude, and (when the route is known) distance to the
  destination. The label appears as a coloured chip in the sidebar and
  the detail panel. Heavies and Super-category aircraft (A380, B748 …)
  also get a warmer accent on the category badge so you can scan by wake
  class without reading the label.
- **Signal strength indicator** — each sidebar row carries a small four-bar
  reception quality indicator driven by the peak signal byte from the
  BEAST feed, turning green for strong signals and red for faint ones.
- **Airline IATA + alliance** — the airline row on each sidebar entry
  carries the IATA code as a small monospace tag, and rows for the three
  major alliances (Star, oneworld, SkyTeam) get a coloured left-border
  accent. Backed by the [OpenFlights](https://openflights.org/data.html)
  airline database baked into the image at build time; no runtime
  network calls.
- **METAR weather at origin / destination** — when a flight has a known
  route, the detail panel surfaces a compact one-line METAR summary
  (wind, visibility, cloud cover) for each end, with the raw METAR on
  hover. Pulled live from the free
  [aviationweather.gov](https://aviationweather.gov/) API and cached
  server-side for 10 min. Disable with `METAR_WEATHER=0`.
- **Airports overlay** — a toggle drops ~2,000 nearest airports onto the
  map as small markers (biggest first so wide views still show the
  majors). Sourced from the OurAirports public-domain database baked
  into the image.
- **Trails persist across restarts** — registry state (aircraft + full
  trails) is checkpointed to `/data/state.json.gz` every 30s and on
  shutdown, so restarting the container doesn't wipe the history. Entries
  older than ~10 minutes at load time are dropped so stale aircraft don't
  reappear.
- **A small HTTP / WebSocket API** if you want to build your own dashboard.

## Before you start

You'll need:

- Docker and `docker compose`.
- A running readsb, dump1090-fa, or ultrafeeder that exposes a **BEAST**
  feed (usually TCP port **30005**).
- Your receiver's latitude and longitude. Flightjar works without them, but
  aircraft take a few seconds longer to appear on the map, and surface
  (on-ground) positions won't decode at all.

## Setup

The easiest path is to pull the prebuilt image from Docker Hub. You don't
need to clone the repo at all — just drop a small `docker-compose.yml`
somewhere and run it.

1. Create `docker-compose.yml`:

   ```yaml
   services:
     flightjar:
       image: mrsuttonmann/flightjar:latest
       container_name: flightjar
       restart: unless-stopped
       ports:
         - "8080:8080"
       environment:
         BEAST_HOST: ultrafeeder        # hostname / IP of your BEAST source
         BEAST_PORT: "30005"
         LAT_REF: "51.0"                # your receiver's coordinates
         LON_REF: "0.0"
       volumes:
         - ./beast-logs:/data           # JSONL output + persisted state + aircraft DB
       networks:
         - ultrafeeder_default          # remove if you aren't using ultrafeeder

   networks:
     ultrafeeder_default:
       external: true
   ```

   The image is multi-arch (linux/amd64 + linux/arm64), so it runs on a
   Raspberry Pi just as well. Each release is also tagged
   `mrsuttonmann/flightjar:git-<short-sha>` for painless rollbacks.

2. Adjust `BEAST_HOST` for your setup. The three common cases:

   - **readsb / ultrafeeder in another compose project on the same host**
     — use its service name (e.g. `ultrafeeder`) and join that project's
     Docker network (as above; change `ultrafeeder_default` to match).
   - **readsb on the same host, port published to localhost** — drop the
     `networks:` block, add `network_mode: host` to the service, and set
     `BEAST_HOST: localhost`.
   - **readsb on a different machine** — drop the `networks:` block and
     point `BEAST_HOST` at its IP or hostname.

3. Start it:

   ```bash
   docker compose up -d
   ```

4. Open the map at [http://localhost:8080](http://localhost:8080) (or wherever
   you've published port 8080).

Logs land in `./beast-logs/beast.jsonl` next to the compose file.

### Building from source

If you want to hack on Flightjar or run a locally-built image, clone the
repo and use the included `docker-compose.yml` (which has `build: .`
instead of `image:`). `docker compose up --build -d` will then build and
launch from source. See the [Development](#development) section below
for the dev loop.

## Using the map

- **Click a plane** (on the map or in the sidebar) to open the detail
  panel. It floats over the left side of the map with a photo of the
  aircraft (when adsbdb has one), its registration, type, operator,
  country flag, the full route, and a grid of live telemetry. The plane
  is auto-followed while the panel is open. Close the panel with the ×
  button, the `Esc` key, or a click on empty map.
- **Hover sync** — hovering a plane on the map highlights its sidebar row
  (and scrolls it into view); hovering a sidebar row draws a ring around
  the aircraft on the map.
- **Sort the sidebar** with the chips at the top: Callsign, Alt, Dist
  (distance from your receiver), or Age. Click the active one again to
  reverse the direction.
- **Units** — the toggle in the header switches the whole UI between
  Metric (km, km/h, m), Imperial (mi, mph, ft), and Nautical (nm, kt, ft).
  Metric altitude flips to km once you cross 1 km.
- **Map layers** — the layers control (top-right of the map) hosts
  everything map-side: base tiles (OpenStreetMap, Carto Dark, Esri
  Satellite) plus the overlays — *Aircraft labels*, *Altitude trails*,
  *Airports*, *Polar coverage* (your receiver's observed max range per
  bearing), and *Range rings* at 50/100/200 NM. All preferences
  persist.
- **Follow + Compact** — two small icon buttons stacked below the
  layers control. Follow auto-enables when a detail panel opens and
  disables when it closes; tap manually to override. Compact hides the
  sidebar; a small `☰ sidebar` button pinned top-left brings it back,
  `C` toggles.
- **Emergency alerts** — aircraft squawking 7500 (hijack), 7600 (radio),
  or 7700 (general) get a red marker outline, a red-tinted sidebar row,
  and a prominent label in the detail panel.
- **Search** — a search box filters the sidebar by callsign or ICAO.
  Press `/` to jump straight into it.
- **Airport tooltips** — tap or hover any ICAO airport code in the route
  line (sidebar or panel) to see the full airport name.
- **Deep links** — the URL fragment tracks the selected aircraft
  (`#icao=4CA2D1`), so you can share a link that pre-selects a plane.
- **Keyboard shortcuts**:
  - `/` — focus the search box
  - `L` — toggle aircraft labels
  - `T` — toggle trails
  - `A` — toggle airports overlay
  - `C` — toggle compact (sidebar-hidden) mode
  - `F` — fit the map to current aircraft
  - `U` — cycle units (Metric → Imperial → Nautical)
  - `Esc` — close the detail panel and clear selection
- **Title bar** shows how many aircraft are currently being tracked (and
  your site name, if set) — handy when the tab is in the background.

## Privacy: hiding your receiver location

By default the receiver is shown as a blue dot at the exact coordinates you
set. If you're sharing screenshots or hosting the map publicly, you can fuzz
that location without affecting how the app decodes positions internally:

```yaml
RECEIVER_ANON_KM: "1"    # snap to a ~1 km grid
# or
RECEIVER_ANON_KM: "10"   # snap to a ~10 km grid
```

When enabled, the displayed receiver shifts to a grid point and a translucent
circle of the chosen radius is drawn around it, so viewers know the true
location is *somewhere* inside that area. Your real coords never leave the
container — they're still used internally to decode aircraft positions
accurately.

## Aircraft database

Flightjar ships with a snapshot of the
[tar1090-db](https://github.com/wiedehopf/tar1090-db) / Mictronics aircraft
registry, downloaded at Docker build time. This gives you
`registration` / `type_icao` / `type_long` on every aircraft in the API
snapshot, and in the sidebar and detail panel.

To refresh without rebuilding the image you have two options:

**Automatic.** Set `AIRCRAFT_DB_REFRESH_HOURS` in the compose file to have
Flightjar re-download the DB itself on a schedule:

```yaml
AIRCRAFT_DB_REFRESH_HOURS: "168"   # weekly
```

The fresh file is written atomically to `/data/aircraft_db.csv.gz` inside
the mounted volume; parsing happens before commit, so a corrupted download
never replaces the live copy.

**Manual.** Drop a file into the mounted `./beast-logs/` directory yourself:

```bash
curl -L -o beast-logs/aircraft_db.csv.gz \
  https://raw.githubusercontent.com/wiedehopf/tar1090-db/refs/heads/csv/aircraft.csv.gz
docker compose restart flightjar
```

If `beast-logs/aircraft_db.csv.gz` exists it wins over the baked copy.
Remove it to fall back to the image's version. If neither is present,
enrichment is silently disabled and the app behaves as before.

## adsbdb integration (routes, aircraft, photos)

Flightjar talks to [adsbdb.com](https://www.adsbdb.com/), a free
community API that needs no account, for two enrichments:

- **Origin / destination by callsign.** Populated on every snapshot for
  aircraft that have broadcast a callsign; shown as `EGLL → KJFK` in the
  sidebar and as a full ticket (with airport names) in the detail panel.
- **Per-tail details on panel open.** When you click a plane, the detail
  panel fetches the aircraft record (registration, type, manufacturer,
  operator, country of registration) and — when one is available —
  displays a photograph at the top. Photos are hotlinked direct from
  [airport-data.com](https://airport-data.com/) (adsbdb's upstream), so
  your browser fetches them without involving this server. The operator
  country flag also surfaces as an emoji in the sidebar next to each
  callsign.

Lookups are serialised with a small spacing (to stay a polite client),
deduplicated, and cached server-side in `./beast-logs/flight_routes.json.gz`.
TTLs: 12h for known routes, 1h for unknown callsigns, 30 days for known
tails, 24h for unknown tails. On first boot you'll see routes, flags and
photos appear gradually as the cache populates.

To disable outbound lookups entirely (offline or privacy-conscious
deploys), set `FLIGHT_ROUTES=0`. That also suppresses photo fetches.

## Running multiple receivers

If you run Flightjar on more than one machine (or want to tell staging apart
from production), set `SITE_NAME` to a short label:

```yaml
SITE_NAME: "Home Receiver"
```

It shows up next to "Flightjar" in the sidebar and in the browser tab title
(e.g. `Flightjar — Home Receiver (7)`).

## Configuration reference

| Setting               | Default             | What it does                                                   |
|-----------------------|---------------------|----------------------------------------------------------------|
| `BEAST_HOST`          | `readsb`            | Hostname or IP of your BEAST source.                           |
| `BEAST_PORT`          | `30005`             | TCP port for the BEAST feed.                                   |
| `LAT_REF`             | (unset)             | Receiver latitude. Faster first fix + surface decoding.        |
| `LON_REF`             | (unset)             | Receiver longitude.                                            |
| `RECEIVER_ANON_KM`    | `0`                 | Fuzz the displayed receiver location (km). `0` = exact.        |
| `SITE_NAME`           | (unset)             | Display name shown in the header and browser tab title.        |
| `BEAST_OUTFILE`       | `/data/beast.jsonl` | Log file inside the container. Empty disables file logging.    |
| `BEAST_ROTATE`        | `daily`             | `none`, `hourly`, or `daily`.                                  |
| `BEAST_ROTATE_KEEP`   | `14`                | How many rotated log files to keep.                            |
| `BEAST_STDOUT`        | `0`                 | Also print messages to the container log (for debugging).      |
| `SNAPSHOT_INTERVAL`   | `1.0`               | How often the map refreshes, in seconds.                       |
| `AIRCRAFT_DB_REFRESH_HOURS` | `0`           | Auto-refresh interval for the aircraft DB. `0` disables.       |
| `FLIGHT_ROUTES`       | `1`                 | Enable origin/destination lookups via adsbdb.com. `0` disables.|
| `METAR_WEATHER`       | `1`                 | Enable METAR lookups via aviationweather.gov. `0` disables.    |

## The log file

Each line is one Mode S / Mode AC message:

```json
{"ts_rx":"2026-04-18T10:15:22.413291+00:00","mlat_ticks":127548213984,"type":"mode_s_long","signal":184,"hex":"8d4ca2d158c901a0c0b8a0cbd1e7"}
```

A couple of `jq` one-liners to get you started:

```bash
# Every message from one specific aircraft (ICAO 8d4ca2d1…)
jq -c 'select(.hex | startswith("8d4ca2d1"))' beast-logs/beast.jsonl

# Rough message rate, grouped by minute
jq -r '.ts_rx[0:16]' beast-logs/beast.jsonl | uniq -c
```

## API

| Path                | Returns                                                            |
|---------------------|--------------------------------------------------------------------|
| `GET  /`            | The map UI.                                                        |
| `GET  /api/aircraft`| Current tracked aircraft, as JSON.                                 |
| `GET  /api/stats`   | Uptime, frame counter, connected WebSocket clients, etc.           |
| `GET  /healthz`     | `200 {"status":"ok"}` when the BEAST feed is connected, `503` otherwise — drop this straight into a Docker `healthcheck:` block. |
| `GET  /metrics`     | Prometheus-format metrics: `flightjar_frames_total`, `flightjar_aircraft_tracked`, `flightjar_websocket_clients`, `flightjar_beast_connected`. |
| `GET  /api/flight/{callsign}` | Origin / destination for a callsign (adsbdb lookup). Returns nulls when the feature is disabled or the callsign is unknown. |
| `GET  /api/aircraft/{icao24}` | Per-tail details (registration, type, operator, photo URLs) for one aircraft, via adsbdb. |
| `GET  /api/airports` | Airports inside a lat/lon bounding box; takes `min_lat`, `min_lon`, `max_lat`, `max_lon`, optional `limit`. |
| `WS   /ws`          | Live aircraft snapshots, one per `SNAPSHOT_INTERVAL`.              |

Each aircraft in the snapshot carries an `emergency` field — `"hijack"`,
`"radio"`, `"general"`, or `null` — derived from squawks 7500/7600/7700,
and a `category` byte from ADS-B TC 4 (1=Light, 2=Small, 3=Large,
4=High-vortex, 5=Heavy, 6=High-performance, 7=Rotorcraft). When
`FLIGHT_ROUTES` is enabled (the default), aircraft also carry `origin`
and `destination` (ICAO airport codes, or `null` if the callsign isn't
in adsbdb's database), plus `operator`, `operator_country`, and
`country_iso` from the per-tail lookup.

Altitude is exposed three ways:
`altitude_baro` (barometric, from DF17 TC 9-18 or DF4/20 surveillance),
`altitude_geo` (geometric / GNSS, from DF17 TC 20-22), and `altitude`
(the best available — prefers baro, falls back to geo). The detail panel
labels the source when only geometric altitude is known, or when the two
disagree by more than 100 ft.

Aircraft also carry `last_seen_mlat` — the BEAST 12 MHz tick counter from
the most recent message — which is useful for sub-second timing between
packets from the same receiver. (It isn't synchronised across receivers,
so don't mix sources.)

Aircraft values are always returned in canonical units (feet, knots, ft/min)
so any client can convert them as it likes. Each aircraft also carries a
`distance_km` field computed against the displayed receiver position
(respecting `RECEIVER_ANON_KM` if set), and the snapshot includes `receiver`
and `site_name` at the top level.

## Development

If you want to hack on Flightjar, the dev tooling is wired in via
`pyproject.toml` and `requirements-dev.txt`:

```bash
pip install -r requirements-dev.txt
ruff check .            # lint
ruff format .           # apply formatting
mypy                    # type-check app/
pytest                  # run the backend test suite
node --test tests/js/   # run the frontend test suite (Node 20+)
```

The frontend is split into small ES modules under `app/static/` —
`format.js`, `units.js`, `altitude.js`, `trend.js` — so
the pure helpers are unit-testable without a browser. `app.js` is the
entrypoint and imports the rest.

`tar1090_shapes.js` (the per-type SVG silhouette bundle) and
`airports.csv` / `aircraft_db.csv.gz` aren't committed — they're
auto-generated at Docker build. If you're running the FastAPI app
outside Docker, regenerate them once:

```bash
python scripts/fetch_plane_shapes.py    # writes app/static/tar1090_shapes.js
# (and similarly for the airport/aircraft DBs if you want them locally)
```

GitHub Actions runs all of the above on every push and pull request, and
only builds + publishes the multi-arch Docker image to Docker Hub when
those checks pass — so `:latest` on Docker Hub always points at a commit
that has cleared CI. A manual `workflow_dispatch` is also available for
one-off publishes (e.g. to add a `v1.2.3` tag alongside `:latest`), and
pushing a `v*` git tag publishes the matching image tag automatically.

### Configuration is validated at startup

Env vars are parsed into a typed `Config` object (`app/config.py`). A bad
`BEAST_PORT`, an unknown `BEAST_ROTATE` value, a negative `BEAST_ROTATE_KEEP`
or a zero `SNAPSHOT_INTERVAL` produces a clear `ConfigError` at startup
rather than silently falling back or crashing deeper in the stack.

Optional floats (`LAT_REF`, `LON_REF`, `RECEIVER_ANON_KM`) stay lenient:
a malformed value is treated as unset.

## Troubleshooting

- **No aircraft appear, status shows "Connected".** Your receiver is reachable
  but isn't sending anything decodable yet. Give it a minute — or check that
  your SDR is actually picking up traffic (e.g. via your existing decoder's
  own web UI).
- **Status shows "Disconnected, retrying…".** Flightjar can't reach
  `BEAST_HOST:BEAST_PORT`. Double-check the hostname/IP and that the BEAST
  port is exposed. `docker compose logs -f flightjar` usually makes the
  reason obvious.
- **Aircraft show up as dots with no callsign/altitude for a while.** Normal
  — it can take a few messages before an aircraft reveals its callsign.
  Setting `LAT_REF` / `LON_REF` speeds this up noticeably.
- **The receiver dot is in the wrong place.** Check `LAT_REF` / `LON_REF` are
  the right way round, and remember `RECEIVER_ANON_KM` deliberately shifts
  the dot if it's non-zero.

## Links

- **Source**: [github.com/MrSuttonmann/flightjar](https://github.com/MrSuttonmann/flightjar) — issues and PRs welcome.
- **Support**: [Buy me a coffee](https://www.buymeacoffee.com/mrsuttonmann) if Flightjar is useful to you.

Both are also linked from the footer of the sidebar in the UI.

## License

Flightjar is released under the **GNU General Public License v3.0** — see
[`LICENSE`](LICENSE) for the full text.

GPL-3.0 was chosen to match [pyModeS](https://github.com/junzis/pyModeS), the
Mode S / ADS-B decoding library Flightjar depends on, which is itself
GPL-3.0-or-later. You're free to use, modify, and redistribute Flightjar; any
redistributed derivative must be made available under the same terms.
