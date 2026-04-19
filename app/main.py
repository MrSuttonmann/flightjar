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

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, JSONResponse

from .aircraft import AircraftRegistry
from .beast import iter_frames

log = logging.getLogger("beast")
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
    stream=sys.stderr,
)


def env_bool(name: str, default: str) -> bool:
    return os.environ.get(name, default).lower() in ("1", "true", "yes", "on")


def env_float(name: str) -> float | None:
    v = os.environ.get(name, "").strip()
    if not v:
        return None
    try:
        return float(v)
    except ValueError:
        log.warning("ignoring non-numeric %s=%r", name, v)
        return None


# ---------------- config ----------------

BEAST_HOST = os.environ.get("BEAST_HOST", "readsb")
BEAST_PORT = int(os.environ.get("BEAST_PORT", "30005"))
LAT_REF = env_float("LAT_REF")
LON_REF = env_float("LON_REF")
RECEIVER_ANON_KM = env_float("RECEIVER_ANON_KM") or 0.0
SITE_NAME = os.environ.get("SITE_NAME", "").strip() or None


def _snap_receiver(lat, lon, anon_km):
    """Snap to a ~anon_km grid so the true position never leaves the process."""
    if lat is None or lon is None or anon_km <= 0:
        return lat, lon
    step = (
        anon_km / 111.0
    )  # ~111 km per degree; slightly over-anonymises longitude at high lat, which is fine
    return round(lat / step) * step, round(lon / step) * step


_DISPLAY_LAT, _DISPLAY_LON = _snap_receiver(LAT_REF, LON_REF, RECEIVER_ANON_KM)
RECEIVER_INFO = {
    "lat": _DISPLAY_LAT,
    "lon": _DISPLAY_LON,
    "anon_km": RECEIVER_ANON_KM,
}

JSONL_PATH = os.environ.get("BEAST_OUTFILE", "/data/beast.jsonl").strip()
JSONL_ROTATE = os.environ.get("BEAST_ROTATE", "daily")
JSONL_KEEP = int(os.environ.get("BEAST_ROTATE_KEEP", "14"))
JSONL_STDOUT = env_bool("BEAST_STDOUT", "0")
JSONL_DECODE = not env_bool("BEAST_NO_DECODE", "0")

SNAPSHOT_INTERVAL = float(os.environ.get("SNAPSHOT_INTERVAL", "1.0"))


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
    lat_ref=LAT_REF,
    lon_ref=LON_REF,
    receiver_info=RECEIVER_INFO,
    site_name=SITE_NAME,
)
jsonl = JsonlWriter(JSONL_PATH, JSONL_ROTATE, JSONL_KEEP, JSONL_STDOUT)
broadcaster = Broadcaster()
stats = {"frames": 0, "started": time.time()}


# ---------------- background tasks ----------------


async def beast_consumer():
    backoff = 1
    while True:
        try:
            log.info("connecting to %s:%d", BEAST_HOST, BEAST_PORT)
            reader, writer = await asyncio.open_connection(BEAST_HOST, BEAST_PORT)
            log.info("connected")
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
                writer.close()
                with contextlib.suppress(Exception):
                    await writer.wait_closed()
            log.warning("BEAST stream closed by remote")
        except asyncio.CancelledError:
            raise
        except Exception as e:
            log.warning("BEAST connection error: %s", e)
        await asyncio.sleep(backoff)
        backoff = min(backoff * 2, 30)


async def snapshot_pusher():
    while True:
        try:
            await asyncio.sleep(SNAPSHOT_INTERVAL)
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
        BEAST_HOST,
        BEAST_PORT,
        LAT_REF,
        LON_REF,
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
        "beast_target": f"{BEAST_HOST}:{BEAST_PORT}",
        "receiver": RECEIVER_INFO,
        "site_name": SITE_NAME,
    }


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
