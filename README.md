# Flightjar

A small web app that shows the aircraft your ADS-B receiver can see, on a
live map — with a rolling log of every message to disk for later analysis.

It reads the BEAST feed from a running readsb, dump1090, or ultrafeeder
instance — so you can point it at whatever's already decoding ADS-B on your
network and get a lightweight map, log file, and simple API on top.

![Flightjar screenshot placeholder]

## What you get

- **Live map** at `http://<host>:8080/` with type-specific plane silhouettes
  (widebody / jet / turboprop / light / helicopter, falling back to ADS-B
  category), altitude-coloured trails showing each aircraft's recent
  altitude history, and a toggleable callsign label on each one.
- **Sidebar list** of currently tracked aircraft, sortable by callsign,
  altitude, distance from the receiver, or age. Hover a plane on the map
  to highlight its row, and vice-versa.
- **Unit switcher** — Metric, Imperial, or Nautical — applied across
  altitude, speed, vertical rate, and distance. Your choice is remembered.
- **A record of every message** written as JSON Lines to a file on disk,
  rotated daily.
- **Optional privacy** — you can fuzz the displayed receiver location so
  sharing screenshots doesn't pin your home address on a map.
- **Optional site name** shown in the header and browser tab so you can
  tell multiple installs apart at a glance.
- **Aircraft DB enrichment** — each aircraft is tagged with its
  registration and type (e.g. `G-ABCD · BOEING 737-800`), so the popup and
  sidebar show the actual tail number rather than just the ICAO hex.
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

1. Clone the repo and open `docker-compose.yml`.
2. Set your receiver coordinates:

   ```yaml
   LAT_REF: "52.98234"
   LON_REF: "-1.20415"
   ```

3. Point `BEAST_HOST` at your BEAST source. The most common cases:

   - **readsb / ultrafeeder running in another compose project on the same
     host** — use its service name (e.g. `ultrafeeder`) and attach
     Flightjar to that project's Docker network. The default compose file
     assumes `ultrafeeder_default`; adjust the `networks:` block at the
     bottom if yours is different.
   - **readsb on the same host, port published to localhost** — uncomment
     `network_mode: host` in `docker-compose.yml` and set
     `BEAST_HOST: localhost`.
   - **readsb on a different machine** — set `BEAST_HOST` to its IP or
     hostname.

4. Start it:

   ```bash
   docker compose up --build -d
   ```

5. Open the map at [http://localhost:8080](http://localhost:8080) (or wherever
   you've published port 8080).

Logs land in `./beast-logs/beast.jsonl` next to the compose file by default.

## Using the map

- **Click a plane** (on the map or in the sidebar) to centre on it and see
  speed, altitude, heading, vertical rate and squawk.
- **Hover sync** — hovering a plane on the map highlights its sidebar row
  (and scrolls it into view); hovering a sidebar row draws a ring around
  the aircraft on the map.
- **Sort the sidebar** with the chips at the top: Callsign, Alt, Dist
  (distance from your receiver), or Age. Click the active one again to
  reverse the direction.
- **Units** — the toggle in the header switches the whole UI between
  Metric (km, km/h, m), Imperial (mi, mph, ft), and Nautical (nm, kt, ft).
  Metric altitude flips to km once you cross 1 km.
- **Labels** — the Labels button toggles the permanent callsign labels
  next to each plane on the map. Your preference is remembered.
- **Trails** — the Trails button toggles altitude-coloured trails for
  every aircraft. Same persistence.
- **Follow** — when a plane is selected and Follow is on, the map
  auto-pans to keep it centred. Toggle off for a static view.
- **Compact** — hides the sidebar for a full-map view. A small
  `☰ sidebar` button pinned top-left brings it back; `C` toggles.
- **Base map** — the layers control (top-right of the map) swaps between
  OpenStreetMap, Carto Dark, and Esri Satellite tiles. Choice is remembered.
- **Range rings** — optional overlay at 50 / 100 / 200 NM around the
  receiver, toggled from the same control.
- **Emergency alerts** — aircraft squawking 7500 (hijack), 7600 (radio),
  or 7700 (general) get a red marker outline, a red-tinted sidebar row,
  and a prominent label in the popup.
- **Search** — a search box filters the sidebar by callsign or ICAO.
  Press `/` to jump straight into it.
- **Deep links** — the URL fragment tracks the selected aircraft
  (`#icao=4CA2D1`), so you can share a link that pre-selects a plane.
- **Keyboard shortcuts**:
  - `/` — focus the search box
  - `L` — toggle aircraft labels
  - `T` — toggle trails
  - `C` — toggle compact (sidebar-hidden) mode
  - `F` — fit the map to current aircraft
  - `U` — cycle units (Metric → Imperial → Nautical)
  - `Esc` — close the popup and clear selection
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
snapshot, and in the sidebar/popup UI.

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
  https://github.com/wiedehopf/tar1090-db/raw/refs/heads/csv/aircraft.csv.gz
docker compose restart flightjar
```

If `beast-logs/aircraft_db.csv.gz` exists it wins over the baked copy.
Remove it to fall back to the image's version. If neither is present,
enrichment is silently disabled and the app behaves as before.

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
| `WS   /ws`          | Live aircraft snapshots, one per `SNAPSHOT_INTERVAL`.              |

Each aircraft in the snapshot carries an `emergency` field — `"hijack"`,
`"radio"`, `"general"`, or `null` — derived from squawks 7500/7600/7700.

Altitude is exposed three ways:
`altitude_baro` (barometric, from DF17 TC 9-18 or DF4/20 surveillance),
`altitude_geo` (geometric / GNSS, from DF17 TC 20-22), and `altitude`
(the best available — prefers baro, falls back to geo). The popup labels
the source when only geometric altitude is known, or when the two disagree
by more than 100 ft.

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
`format.js`, `units.js`, `altitude.js`, `trend.js`, `silhouette.js` — so
the pure helpers are unit-testable without a browser. `app.js` is the
entrypoint and imports the rest.

GitHub Actions runs all of the above on every push and pull request.

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
