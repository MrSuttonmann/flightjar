"""FastAPI app combining the BEAST consumer, JSONL logger, and live map UI."""

import asyncio
import contextlib
import json
import logging
import os
import sys
import time
from collections.abc import Callable
from contextlib import asynccontextmanager
from datetime import UTC, datetime
from logging.handlers import TimedRotatingFileHandler
from pathlib import Path
from typing import Any

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, JSONResponse, PlainTextResponse

from .aircraft import AircraftRegistry
from .beast import iter_frames
from .config import Config

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

registry = AircraftRegistry(
    lat_ref=cfg.lat_ref,
    lon_ref=cfg.lon_ref,
    receiver_info=RECEIVER_INFO,
    site_name=cfg.site_name,
)
jsonl = JsonlWriter(cfg.jsonl_path, cfg.jsonl_rotate, cfg.jsonl_keep, cfg.jsonl_stdout)
broadcaster = Broadcaster()
stats: dict[str, Any] = {"frames": 0, "started": time.time(), "beast_connected": False}


# ---------------- background tasks ----------------


async def beast_consumer():
    backoff = 1
    while True:
        try:
            log.info("connecting to %s:%d", cfg.beast_host, cfg.beast_port)
            reader, writer = await asyncio.open_connection(cfg.beast_host, cfg.beast_port)
            log.info("connected")
            stats["beast_connected"] = True
            backoff = 1
            try:
                async for frame in iter_frames(reader):
                    type_name, mlat_ticks, sig, msg_bytes = frame
                    hex_msg = msg_bytes.hex()
                    now = time.time()
                    stats["frames"] += 1

                    if type_name in ("mode_s_short", "mode_s_long"):
                        registry.ingest(hex_msg, now)

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
                stats["beast_connected"] = False
                writer.close()
                with contextlib.suppress(Exception):
                    await writer.wait_closed()
            log.warning("BEAST stream closed by remote")
        except asyncio.CancelledError:
            raise
        except Exception as e:
            stats["beast_connected"] = False
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
    consumer = asyncio.create_task(beast_consumer(), name="beast_consumer")
    pusher = asyncio.create_task(snapshot_pusher(), name="snapshot_pusher")
    try:
        yield
    finally:
        for t in (consumer, pusher):
            t.cancel()
        await asyncio.gather(consumer, pusher, return_exceptions=True)
        jsonl.close()


app = FastAPI(lifespan=lifespan, title="Flightjar")

STATIC_DIR = Path(__file__).parent / "static"


@app.get("/")
async def root():
    return FileResponse(STATIC_DIR / "index.html")


@app.get("/api/aircraft")
async def api_aircraft():
    return JSONResponse(registry.snapshot())


@app.get("/api/stats")
async def api_stats():
    return {
        "frames": stats["frames"],
        "uptime_s": round(time.time() - stats["started"], 1),
        "aircraft_tracked": len(registry.aircraft),
        "websocket_clients": len(broadcaster.clients),
        "beast_target": f"{cfg.beast_host}:{cfg.beast_port}",
        "beast_connected": bool(stats["beast_connected"]),
        "receiver": RECEIVER_INFO,
        "site_name": cfg.site_name,
    }


@app.get("/healthz")
async def healthz():
    if stats["beast_connected"]:
        return {"status": "ok"}
    return JSONResponse({"status": "disconnected"}, status_code=503)


@app.get("/metrics")
async def metrics():
    connected = 1 if stats["beast_connected"] else 0
    body = (
        "# HELP flightjar_frames_total BEAST frames received since startup\n"
        "# TYPE flightjar_frames_total counter\n"
        f"flightjar_frames_total {stats['frames']}\n"
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
