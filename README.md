# beast-logger

Docker container that connects to a BEAST TCP source (the one that feeds
tar1090 — usually readsb or dump1090-fa on port **30005**) and gives you:

1. **A live map UI** at `http://host:8080/` — Leaflet + OSM, planes coloured
   by altitude, trails behind them, click for details.
2. **JSONL logging** of every Mode S/AC message to `/data/beast.jsonl`,
   rotated daily (or hourly).
3. **A small JSON API** (`/api/aircraft`, `/api/stats`) and a **WebSocket**
   stream at `/ws` if you want to plug in your own clients.

Single Python process inside one container. No GPU, no SDR — it's a pure
BEAST consumer.

## Quick start

```bash
# Edit docker-compose.yml so BEAST_HOST points at your readsb/dump1090.
docker compose up --build -d

# Map UI:
open http://localhost:8080

# JSONL files land in ./beast-logs/beast.jsonl
```

## What you see on the map

- Triangle marker per aircraft, rotated to its ground track.
- Colour scale from red (low) → yellow → green → blue (high), 0–40,000 ft.
- A polyline trail of recent positions (last ~60 fixes, ~1 minute).
- Sidebar list sorted by most-recent message; click to centre and open the
  popup.
- Aircraft drop off the map ~60s after their last message.

A note on first-fix time: ADS-B position decode (CPR) needs a fresh
even+odd message pair, so a brand-new aircraft typically takes a couple of
seconds to appear with a position. Setting `LAT_REF`/`LON_REF` (your
receiver coordinates) lets the decoder do *local* decode from a single
message, which makes positions appear instantly and is required for any
surface-position decoding.

## Which port to point at

`tar1090` is just the web UI; BEAST output comes from the decoder underneath:

| Decoder              | BEAST out port |
|----------------------|----------------|
| readsb               | 30005          |
| dump1090-fa          | 30005          |
| dump1090-mutability  | 30005          |

## Networking

Three common setups:

1. **readsb on the same host, port published** — set `BEAST_HOST` to the
   host IP / `host.docker.internal`, or uncomment `network_mode: host`.
2. **readsb in another compose project on the same host** — join its
   network. `docker network ls` to find the name, then uncomment the
   `networks:` block in `docker-compose.yml` and put the right name in.
   `BEAST_HOST` is then the service name (`readsb`, `ultrafeeder`, etc.).
3. **readsb on a different host** — set `BEAST_HOST` to its IP/hostname.

## JSONL format

Each line is one Mode S/AC message:

```json
{"ts_rx":"2026-04-18T10:15:22.413291+00:00","mlat_ticks":127548213984,"type":"mode_s_long","signal":184,"hex":"8d4ca2d158c901a0c0b8a0cbd1e7"}
```

Querying with `jq`:

```bash
# All messages from one aircraft
jq -c 'select(.hex | startswith("8d4ca2d1"))' beast-logs/beast.jsonl

# Message rate by minute
jq -r '.ts_rx[0:16]' beast-logs/beast.jsonl | uniq -c
```

If you want decoded fields in the JSONL too, that's currently in the
aircraft state but not the per-message log. Easy to add — drop me a line
or look at `app/aircraft.py` for the decoding helpers.

## API endpoints

| Path             | Description                                           |
|------------------|-------------------------------------------------------|
| `GET /`          | Map UI                                                |
| `GET /api/aircraft` | Current snapshot of all tracked aircraft (JSON)    |
| `GET /api/stats` | Frame counter, uptime, websocket client count, etc.   |
| `WS  /ws`        | Push channel — one snapshot per second                |

## Config reference

| Env var             | Default             | Meaning                                      |
|---------------------|---------------------|----------------------------------------------|
| `BEAST_HOST`        | `readsb`            | BEAST source hostname/IP.                    |
| `BEAST_PORT`        | `30005`             | BEAST TCP port.                              |
| `LAT_REF`           | (unset)             | Receiver latitude — speeds up first fix.     |
| `LON_REF`           | (unset)             | Receiver longitude.                          |
| `BEAST_OUTFILE`     | `/data/beast.jsonl` | JSONL output path. Empty string disables.    |
| `BEAST_ROTATE`      | `daily`             | `none`, `hourly`, or `daily`.                |
| `BEAST_ROTATE_KEEP` | `14`                | Number of rotated files to keep.             |
| `BEAST_STDOUT`      | `0`                 | Mirror JSONL to stdout (docker logs).        |
| `SNAPSHOT_INTERVAL` | `1.0`               | Seconds between WebSocket snapshot pushes.   |

## Files

```
beast-logger/
├── Dockerfile
├── docker-compose.yml
├── requirements.txt
├── README.md
└── app/
    ├── __init__.py
    ├── beast.py        # BEAST wire format parser
    ├── aircraft.py     # Per-ICAO state, CPR position decoding
    ├── main.py         # FastAPI app, BEAST consumer, broadcaster
    └── static/
        └── index.html  # Leaflet map UI
```
