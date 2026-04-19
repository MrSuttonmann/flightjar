"""FastAPI app combining the BEAST consumer, JSONL logger, and live map UI."""

import asyncio
import contextlib
import hashlib
import json
import logging
import os
import sys
import time
from collections.abc import Callable
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from datetime import UTC, datetime
from logging.handlers import TimedRotatingFileHandler
from pathlib import Path

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import HTMLResponse, JSONResponse, PlainTextResponse
from fastapi.staticfiles import StaticFiles

from .aircraft import AircraftRegistry
from .aircraft_db import AircraftDB
from .beast import iter_frames
from .config import Config
from .persistence import load_state, save_state

log = logging.getLogger("beast")
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
    stream=sys.stderr,
)


# ---------------- config ----------------

cfg = Config.from_env()


def _snap_receiver(lat, lon, anon_km):
    """Snap to a ~anon_km grid so the true position never leaves the process."""
    if lat is None or lon is None or anon_km <= 0:
        return lat, lon
    # ~111 km per degree; slightly over-anonymises longitude at high lat, which is fine.
    step = anon_km / 111.0
    return round(lat / step) * step, round(lon / step) * step


_DISPLAY_LAT, _DISPLAY_LON = _snap_receiver(cfg.lat_ref, cfg.lon_ref, cfg.receiver_anon_km)
RECEIVER_INFO = {
    "lat": _DISPLAY_LAT,
    "lon": _DISPLAY_LON,
    "anon_km": cfg.receiver_anon_km,
}


# ---------------- jsonl writer ----------------


class JsonlWriter:
    def __init__(self, path: str, rotate: str, keep: int, stdout: bool):
        self.writers: list[Callable[[str], object]] = []
        # Handles we own and must close on shutdown.
        self._closers: list[Callable[[], object]] = []
        if path:
            d = os.path.dirname(path)
            if d:
                os.makedirs(d, exist_ok=True)
            if rotate == "none":
                # File stays open for the process lifetime; close() handles it.
                fh = open(path, "a", buffering=1)  # noqa: SIM115
                self.writers.append(lambda s: fh.write(s + "\n"))
                self._closers.append(fh.close)
            else:
                when = "H" if rotate == "hourly" else "midnight"
                handler = TimedRotatingFileHandler(
                    path,
                    when=when,
                    backupCount=keep,
                    utc=True,
                )
                handler.setFormatter(logging.Formatter("%(message)s"))
                file_log = logging.getLogger("beast.jsonl")
                file_log.setLevel(logging.INFO)
                file_log.propagate = False
                file_log.addHandler(handler)
                self.writers.append(file_log.info)
                self._closers.append(handler.close)
        if stdout:
            self.writers.append(lambda s: print(s, flush=True))

    @property
    def enabled(self) -> bool:
        return bool(self.writers)

    def write(self, obj: dict):
        if not self.writers:
            return
        line = json.dumps(obj, separators=(",", ":"))
        for w in self.writers:
            try:
                w(line)
            except Exception as e:
                log.error("jsonl writer failure: %s", e)

    def close(self) -> None:
        for fn in self._closers:
            with contextlib.suppress(Exception):
                fn()
        self._closers.clear()


# ---------------- websocket broadcast ----------------


class Broadcaster:
    def __init__(self):
        self.clients: set[WebSocket] = set()

    def add(self, ws: WebSocket):
        self.clients.add(ws)

    def remove(self, ws: WebSocket):
        self.clients.discard(ws)

    async def broadcast(self, payload: str):
        if not self.clients:
            return
        dead = []
        for ws in list(self.clients):
            try:
                await ws.send_text(payload)
            except Exception:
                dead.append(ws)
        for ws in dead:
            self.clients.discard(ws)


# ---------------- shared state ----------------

aircraft_db = AircraftDB()
registry = AircraftRegistry(
    lat_ref=cfg.lat_ref,
    lon_ref=cfg.lon_ref,
    receiver_info=RECEIVER_INFO,
    site_name=cfg.site_name,
    aircraft_db=aircraft_db,
)


@dataclass
class Stats:
    frames: int = 0
    beast_connected: bool = False
    started: float = field(default_factory=time.time)


jsonl = JsonlWriter(cfg.jsonl_path, cfg.jsonl_rotate, cfg.jsonl_keep, cfg.jsonl_stdout)
broadcaster = Broadcaster()
stats = Stats()


# ---------------- background tasks ----------------


async def beast_consumer():
    backoff = 1
    while True:
        try:
            log.info("connecting to %s:%d", cfg.beast_host, cfg.beast_port)
            reader, writer = await asyncio.open_connection(cfg.beast_host, cfg.beast_port)
            log.info("connected")
            stats.beast_connected = True
            backoff = 1
            try:
                async for frame in iter_frames(reader):
                    type_name, mlat_ticks, sig, msg_bytes = frame
                    hex_msg = msg_bytes.hex()
                    now = time.time()
                    stats.frames += 1

                    if type_name in ("mode_s_short", "mode_s_long"):
                        registry.ingest(hex_msg, now, mlat_ticks=mlat_ticks)

                    if jsonl.enabled:
                        record = {
                            "ts_rx": datetime.now(UTC).isoformat(),
                            "mlat_ticks": mlat_ticks,
                            "type": type_name,
                            "signal": sig,
                            "hex": hex_msg,
                        }
                        jsonl.write(record)
            finally:
                stats.beast_connected = False
                writer.close()
                with contextlib.suppress(Exception):
                    await writer.wait_closed()
            log.warning("BEAST stream closed by remote")
        except asyncio.CancelledError:
            raise
        except Exception as e:
            stats.beast_connected = False
            log.warning("BEAST connection error: %s", e)
        await asyncio.sleep(backoff)
        backoff = min(backoff * 2, 30)


async def snapshot_pusher():
    while True:
        try:
            await asyncio.sleep(cfg.snapshot_interval)
            now = time.time()
            registry.cleanup(now)
            if broadcaster.clients:
                snap = registry.snapshot(now)
                payload = json.dumps(snap, separators=(",", ":"))
                await broadcaster.broadcast(payload)
        except asyncio.CancelledError:
            raise
        except Exception as e:
            log.error("snapshot pusher error: %s", e)


STATE_PATH = Path(cfg.jsonl_path).parent / "state.json.gz" if cfg.jsonl_path else None
STATE_SAVE_INTERVAL = 30.0


async def state_persister():
    """Write the registry to disk every STATE_SAVE_INTERVAL seconds."""
    if STATE_PATH is None:
        return
    while True:
        try:
            await asyncio.sleep(STATE_SAVE_INTERVAL)
            await asyncio.to_thread(save_state, registry, STATE_PATH)
        except asyncio.CancelledError:
            raise
        except Exception as e:
            log.warning("state persister error: %s", e)


# ---------------- app ----------------


@asynccontextmanager
async def lifespan(app: FastAPI):
    log.info(
        "starting Flightjar (BEAST=%s:%d, ref=%s,%s, jsonl=%s)",
        cfg.beast_host,
        cfg.beast_port,
        cfg.lat_ref,
        cfg.lon_ref,
        jsonl.enabled,
    )
    # Restore persisted registry state (aircraft + trails) so the UI has
    # history to show immediately after restart.
    if STATE_PATH is not None:
        try:
            load_state(registry, STATE_PATH)
        except Exception as e:
            log.warning("state load failed: %s", e)

    # Load the aircraft DB off the event loop (large file, pure CPU).
    db_task = asyncio.create_task(
        asyncio.to_thread(aircraft_db.load_first_available),
        name="aircraft_db_loader",
    )
    consumer = asyncio.create_task(beast_consumer(), name="beast_consumer")
    pusher = asyncio.create_task(snapshot_pusher(), name="snapshot_pusher")
    persister = asyncio.create_task(state_persister(), name="state_persister")
    try:
        yield
    finally:
        for t in (consumer, pusher, db_task, persister):
            t.cancel()
        await asyncio.gather(consumer, pusher, db_task, persister, return_exceptions=True)
        # One last save so we don't lose the gap between the final periodic
        # write and shutdown.
        if STATE_PATH is not None:
            try:
                save_state(registry, STATE_PATH)
            except Exception as e:
                log.warning("final state save failed: %s", e)
        jsonl.close()


app = FastAPI(
    lifespan=lifespan,
    title="Flightjar",
    description=(
        "Live ADS-B map + JSONL logger on top of a BEAST feed.\n\n"
        "• `GET /` — the map UI.\n"
        "• `GET /api/aircraft` — current tracked aircraft.\n"
        "• `GET /api/stats` — uptime, frame counter, WebSocket clients.\n"
        "• `GET /healthz` — Docker-healthcheck-friendly liveness probe.\n"
        "• `GET /metrics` — Prometheus text format.\n"
        "• `WS  /ws` — push channel for aircraft snapshots.\n"
    ),
)

STATIC_DIR = Path(__file__).parent / "static"
app.mount("/static", StaticFiles(directory=STATIC_DIR), name="static")


def _asset_hash(name: str) -> str:
    """Short content hash of a static asset, used for cache-busting URLs."""
    return hashlib.sha256((STATIC_DIR / name).read_bytes()).hexdigest()[:12]


def _render_index() -> str:
    """Read index.html and inject per-asset content hashes into its URLs."""
    template = (STATIC_DIR / "index.html").read_text()
    return template.replace("__CSS_V__", _asset_hash("app.css")).replace(
        "__JS_V__", _asset_hash("app.js")
    )


INDEX_HTML = _render_index()


@app.get("/", summary="Map UI", response_class=HTMLResponse, include_in_schema=False)
async def root():
    """Return the single-page Leaflet UI with cache-busting asset URLs."""
    return HTMLResponse(INDEX_HTML)


@app.get("/api/aircraft", summary="Current tracked aircraft")
async def api_aircraft():
    """The same snapshot broadcast over the WebSocket.

    Units: altitudes in feet, speeds in knots, vertical rate in ft/min,
    distance_km in km. Each aircraft carries `altitude_baro`, `altitude_geo`,
    and `altitude` (best-known); an `emergency` label when squawking
    7500/7600/7700; and a full trail of `[lat, lon, alt]` points.
    """
    return JSONResponse(registry.snapshot())


@app.get("/api/stats", summary="App-level metrics as JSON")
async def api_stats():
    """Uptime, frame counter, WebSocket clients, BEAST connection state,
    and the receiver's displayed position (may be anonymised)."""
    return {
        "frames": stats.frames,
        "uptime_s": round(time.time() - stats.started, 1),
        "aircraft_tracked": len(registry.aircraft),
        "websocket_clients": len(broadcaster.clients),
        "beast_target": f"{cfg.beast_host}:{cfg.beast_port}",
        "beast_connected": bool(stats.beast_connected),
        "receiver": RECEIVER_INFO,
        "site_name": cfg.site_name,
    }


@app.get("/healthz", summary="Liveness probe")
async def healthz():
    """Returns 200 when the BEAST feed is connected, 503 otherwise.

    Drop straight into a Docker `healthcheck:` block.
    """
    if stats.beast_connected:
        return {"status": "ok"}
    return JSONResponse({"status": "disconnected"}, status_code=503)


@app.get("/metrics", summary="Prometheus metrics", response_class=PlainTextResponse)
async def metrics():
    """Text-format exposition of frames, aircraft, WebSocket clients, and
    BEAST connection state."""
    connected = 1 if stats.beast_connected else 0
    body = (
        "# HELP flightjar_frames_total BEAST frames received since startup\n"
        "# TYPE flightjar_frames_total counter\n"
        f"flightjar_frames_total {stats.frames}\n"
        "# HELP flightjar_aircraft_tracked Currently tracked aircraft\n"
        "# TYPE flightjar_aircraft_tracked gauge\n"
        f"flightjar_aircraft_tracked {len(registry.aircraft)}\n"
        "# HELP flightjar_websocket_clients Connected WebSocket clients\n"
        "# TYPE flightjar_websocket_clients gauge\n"
        f"flightjar_websocket_clients {len(broadcaster.clients)}\n"
        "# HELP flightjar_beast_connected BEAST feed connection state (0/1)\n"
        "# TYPE flightjar_beast_connected gauge\n"
        f"flightjar_beast_connected {connected}\n"
    )
    return PlainTextResponse(body, media_type="text/plain; version=0.0.4")


@app.websocket("/ws")
async def ws_endpoint(websocket: WebSocket):
    await websocket.accept()
    broadcaster.add(websocket)
    try:
        # send an initial snapshot immediately so the map populates fast
        await websocket.send_text(json.dumps(registry.snapshot(), separators=(",", ":")))
        while True:
            # drain any client messages (we don't expect any, but keep alive)
            await websocket.receive_text()
    except WebSocketDisconnect:
        pass
    except Exception as e:
        log.debug("ws error: %s", e)
    finally:
        broadcaster.remove(websocket)
