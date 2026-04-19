# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Single-process Python service that connects to a BEAST TCP feed (readsb/dump1090-fa,
typically port 30005), decodes Mode S / ADS-B
messages with pyModeS, and exposes: a Leaflet map at `/`, `/api/aircraft`,
`/api/stats`, a `/ws` WebSocket pushing snapshots, and per-message JSONL logging
to `/data/beast.jsonl`.

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
ruff check .       # lint
ruff format .      # apply formatting
mypy               # type-check app/
pytest             # run the test suite
```

## Architecture

Single entry point: `app/main.py` (FastAPI). Map UI + WebSocket snapshots +
JSONL logger + live aircraft registry all run in one asyncio loop.

### Data flow in `app/main.py`

1. `beast_consumer()` task: opens an asyncio TCP connection to `BEAST_HOST:BEAST_PORT`,
   runs `iter_frames()` from `app/beast.py`, and for every frame:
   - Feeds Mode S messages into the shared `AircraftRegistry` (`registry.ingest`).
   - Writes a raw JSONL record via `JsonlWriter` (rotating file handler +/- stdout).
   - Auto-reconnects with exponential backoff (capped at 30s).
2. `snapshot_pusher()` task: every `SNAPSHOT_INTERVAL` seconds, evicts stale
   aircraft and broadcasts a JSON snapshot to every connected WebSocket client.
3. HTTP/WS endpoints share the same `registry` and `broadcaster` singletons —
   no locks, because everything runs on the single asyncio event loop.

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

### Environment variables

Handled in `app/main.py` and the Dockerfile: `BEAST_HOST`, `BEAST_PORT`,
`LAT_REF`, `LON_REF`, `BEAST_OUTFILE` (empty = disable file), `BEAST_ROTATE`
(`none|hourly|daily`), `BEAST_ROTATE_KEEP`, `BEAST_STDOUT`, `BEAST_NO_DECODE`,
`SNAPSHOT_INTERVAL`. The README has the full reference table.

### Networking note

The compose file joins the external `ultrafeeder_default` network so
`BEAST_HOST: ultrafeeder` resolves to the readsb/ultrafeeder container. If
you change to a different deployment (host-mode, remote readsb), the
`networks:` block and `BEAST_HOST` must be updated together.
