# Flightjar

A small web app that shows the aircraft your ADS-B receiver can see, on a
live map — with a rolling log of every message to disk for later analysis.

It reads the BEAST feed from a running readsb, dump1090, or ultrafeeder
instance — so you can point it at whatever's already decoding ADS-B on your
network and get a lightweight map, log file, and simple API on top.

![Flightjar screenshot placeholder]

## What you get

- **Live map** at `http://<host>:8080/` with planes coloured by altitude,
  short trails, and a callsign label on each one.
- **Sidebar list** of currently tracked aircraft, sortable by callsign,
  altitude, distance from the receiver, or age.
- **A record of every message** written as JSON Lines to a file on disk,
  rotated daily.
- **Optional privacy** — you can fuzz the displayed receiver location so
  sharing screenshots doesn't pin your home address on a map.
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
- **Sort the sidebar** with the chips at the top: Callsign, Alt, Dist
  (distance from your receiver), or Age. Click the active one again to
  reverse the direction.
- **Title bar** shows how many aircraft are currently being tracked —
  handy when the tab is in the background.

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

## Configuration reference

| Setting               | Default             | What it does                                                   |
|-----------------------|---------------------|----------------------------------------------------------------|
| `BEAST_HOST`          | `readsb`            | Hostname or IP of your BEAST source.                           |
| `BEAST_PORT`          | `30005`             | TCP port for the BEAST feed.                                   |
| `LAT_REF`             | (unset)             | Receiver latitude. Faster first fix + surface decoding.        |
| `LON_REF`             | (unset)             | Receiver longitude.                                            |
| `RECEIVER_ANON_KM`    | `0`                 | Fuzz the displayed receiver location (km). `0` = exact.        |
| `BEAST_OUTFILE`       | `/data/beast.jsonl` | Log file inside the container. Empty disables file logging.    |
| `BEAST_ROTATE`        | `daily`             | `none`, `hourly`, or `daily`.                                  |
| `BEAST_ROTATE_KEEP`   | `14`                | How many rotated log files to keep.                            |
| `BEAST_STDOUT`        | `0`                 | Also print messages to the container log (for debugging).      |
| `SNAPSHOT_INTERVAL`   | `1.0`               | How often the map refreshes, in seconds.                       |

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

| Path                | Returns                                                   |
|---------------------|-----------------------------------------------------------|
| `GET  /`            | The map UI.                                               |
| `GET  /api/aircraft`| Current tracked aircraft, as JSON.                        |
| `GET  /api/stats`   | Uptime, frame counter, connected websocket clients, etc.  |
| `WS   /ws`          | Live aircraft snapshots, one per `SNAPSHOT_INTERVAL`.     |

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

## License

Flightjar is released under the **GNU General Public License v3.0** — see
[`LICENSE`](LICENSE) for the full text.

GPL-3.0 was chosen to match [pyModeS](https://github.com/junzis/pyModeS), the
Mode S / ADS-B decoding library Flightjar depends on, which is itself
GPL-3.0-or-later. You're free to use, modify, and redistribute Flightjar; any
redistributed derivative must be made available under the same terms.
