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

from .aircraft import AircraftRegistry, flight_phase, is_plausible_route
from .aircraft_db import DEFAULT_REFRESH_URL, AircraftDB
from .airlines_db import AirlinesDB
from .airports_db import AirportsDB
from .alerts import AlertWatcher
from .beast import iter_frames
from .config import Config
from .coverage import PolarCoverage
from .flight_routes import AdsbdbClient
from .heatmap import TrafficHeatmap
from .metar import MetarClient
from .navaids_db import NavaidsDB
from .notifications import NotifierDispatcher
from .notifications_config import NotificationsConfigStore
from .persistence import load_state, save_state
from .photos import PlanespottersClient
from .polar_heatmap import PolarHeatmap
from .vfrmap_cycle import VfrmapCycle
from .watchlist import WatchlistStore

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
        # Fan out in parallel so one stuck client can't stall the snapshot
        # cadence for everyone else.
        clients = list(self.clients)
        results = await asyncio.gather(
            *(ws.send_text(payload) for ws in clients),
            return_exceptions=True,
        )
        for ws, result in zip(clients, results, strict=True):
            if isinstance(result, BaseException):
                self.clients.discard(ws)


# ---------------- shared state ----------------

aircraft_db = AircraftDB()
airports_db = AirportsDB()
navaids_db = NavaidsDB()
airlines_db = AirlinesDB()
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
                        registry.ingest(hex_msg, now, mlat_ticks=mlat_ticks, signal=sig)

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


# Strong references for fire-and-forget background tasks so the garbage
# collector doesn't kill them mid-flight. See PEP-asyncio docs.
_background_tasks: set[asyncio.Task] = set()


def _spawn_background(coro) -> None:
    task = asyncio.create_task(coro)
    _background_tasks.add(task)

    def _done(t: asyncio.Task) -> None:
        _background_tasks.discard(t)
        if t.cancelled():
            return
        exc = t.exception()
        if exc is not None:
            log.warning("background task failed: %r", exc)

    task.add_done_callback(_done)


def build_snapshot(now: float | None = None) -> dict:
    """Registry snapshot plus top-level server stats (e.g. frame counter).

    Kept as a single helper so the initial WebSocket snapshot, the periodic
    broadcast, and the /api/aircraft HTTP response all ship the same shape.

    Enriches each aircraft with `origin` / `destination` from the adsbdb
    cache (synchronous lookup — no network call). When an aircraft has a
    callsign we haven't looked up yet, kicks off a background lookup so
    the next snapshot can fill it in. Dedup + concurrency limiting live
    inside the AdsbdbClient so this stays a one-liner.
    """
    snap = registry.snapshot(now)
    snap["frames"] = stats.frames
    # Coverage observations are wired through registry.on_position so they
    # fire once per real fix (see below). Here we just flush pending writes.
    coverage.maybe_persist(interval=60.0)
    heatmap.maybe_persist(interval=60.0)
    polar_heatmap.maybe_persist(interval=60.0)
    if adsbdb.enabled:
        referenced_airports: set[str] = set()
        for ac in snap["aircraft"]:
            cs = ac.get("callsign")
            route_known, route = adsbdb.lookup_cached_route(cs) if cs else (False, None)
            ac["origin"] = route.get("origin") if route else None
            ac["destination"] = route.get("destination") if route else None
            # Only fire a background fill when the cache has nothing for us;
            # cached-negative entries are deliberately skipped so we don't
            # spawn (and then immediately no-op) a task per aircraft per tick.
            if cs and not route_known:
                _spawn_background(adsbdb.lookup_route(cs))
            if ac["origin"]:
                referenced_airports.add(ac["origin"])
            if ac["destination"]:
                referenced_airports.add(ac["destination"])

            info_known, info = adsbdb.lookup_cached_aircraft(ac["icao"])
            if info:
                ac["operator"] = info.get("operator")
                ac["operator_country"] = info.get("operator_country")
                ac["country_iso"] = info.get("operator_country_iso")
            else:
                ac["operator"] = None
                ac["operator_country"] = None
                ac["country_iso"] = None
            if not info_known:
                _spawn_background(adsbdb.lookup_aircraft(ac["icao"]))
        # One lookup per unique airport — frontend resolves name/lat/lon by code.
        snap["airports"] = {
            code: info for code in referenced_airports if (info := _airport_info(code)) is not None
        }
        # METAR enrichment: attach any cached METAR to each airport entry
        # and kick a batch fetch for anything missing so the next tick
        # has it. Batching means one HTTP call per snapshot that needs
        # a refresh, not one per airport.
        if metar.enabled and snap["airports"]:
            uncached: list[str] = []
            for code, info in snap["airports"].items():
                known, data = metar.lookup_cached(code)
                if known:
                    info["metar"] = data
                else:
                    uncached.append(code)
            if uncached:
                _spawn_background(metar.lookup_many(uncached))
    # Phase classification + airline enrichment run regardless of adsbdb:
    # climb/cruise/descent only need altitude+vrate+on_ground, which are
    # in the base snapshot. 'approach' and airline lookup unlock when
    # their respective inputs are present.
    #
    # Route plausibility runs first so phase classification doesn't use
    # a destination that the physics says can't be right (adsbdb's
    # callsign-keyed routes can be stale across reused callsigns).
    airports_map = snap.get("airports") or {}
    for ac in snap["aircraft"]:
        origin_code = ac.get("origin")
        dest_code = ac.get("destination")
        origin_info = airports_map.get(origin_code) if origin_code else None
        dest_info = airports_map.get(dest_code) if dest_code else None
        if origin_info and dest_info and not is_plausible_route(ac, origin_info, dest_info):
            log.debug(
                "dropping implausible route %s→%s for %s (track=%s pos=%s,%s)",
                origin_code,
                dest_code,
                ac["icao"],
                ac.get("track"),
                ac.get("lat"),
                ac.get("lon"),
            )
            ac["origin"] = None
            ac["destination"] = None
            dest_info = None
        ac["phase"] = flight_phase(ac, dest_info)
        # Note the last-seen timestamp for any watchlisted tail in
        # coverage so the watchlist dialog can show "last seen Xh
        # ago" for out-of-range entries. Non-watchlisted icaos are a
        # no-op inside record_seen.
        watchlist_store.record_seen(ac["icao"], ac.get("last_seen"))
        airline = airlines_db.lookup_by_callsign(ac.get("callsign"))
        if airline:
            ac["operator_iata"] = airline.get("iata")
            ac["operator_icao"] = airline.get("icao")
            ac["operator_alliance"] = airline.get("alliance")
            # Fall back to OpenFlights' name when adsbdb didn't supply one.
            if not ac.get("operator") and airline.get("name"):
                ac["operator"] = airline["name"]
        else:
            ac["operator_iata"] = None
            ac["operator_icao"] = None
            ac["operator_alliance"] = None
    # Drain and emit any one-shot easter-egg events queued since the
    # last tick (see `_record_range_event`). Queue is shared across WS
    # clients — whoever collects it next wins the toast.
    if _pending_egg_events:
        snap["events"] = _pending_egg_events[:]
        _pending_egg_events.clear()
    return snap


def _airport_info(icao: str | None) -> dict | None:
    """Return {name, lat, lon} for an airport code, or None if we don't know."""
    info = airports_db.lookup(icao)
    if not info:
        return None
    # Prefer "Name, City" when the city adds clarity (often it's different).
    name = info.get("name") or ""
    city = info.get("city") or ""
    display = f"{name}, {city}" if city and city.lower() not in name.lower() else name
    entry: dict = {"name": display or None}
    if "lat" in info and "lon" in info:
        entry["lat"] = info["lat"]
        entry["lon"] = info["lon"]
    return entry


async def snapshot_pusher():
    while True:
        try:
            await asyncio.sleep(cfg.snapshot_interval)
            now = time.time()
            registry.cleanup(now)
            # Always build the snapshot so the alert watcher can fire
            # notifications even when no browser tab is connected.
            # Broadcast only runs when there's a client to receive it.
            snap = build_snapshot(now)
            # Fire-and-forget so a slow Telegram/ntfy/webhook round-trip
            # can't stall the 1 Hz broadcast cadence. _spawn_background
            # logs any uncaught task exceptions via its done-callback.
            _spawn_background(alerts.observe(snap))
            if broadcaster.clients:
                payload = json.dumps(snap, separators=(",", ":"))
                await broadcaster.broadcast(payload)
        except asyncio.CancelledError:
            raise
        except Exception as e:
            log.error("snapshot pusher error: %s", e)


STATE_PATH = Path(cfg.jsonl_path).parent / "state.json.gz" if cfg.jsonl_path else None
STATE_SAVE_INTERVAL = 30.0

# Where a user-provided aircraft DB lives; also where the auto-refresh writes.
AIRCRAFT_DB_PATH = Path(cfg.jsonl_path).parent / "aircraft_db.csv.gz" if cfg.jsonl_path else None

# Persistent cache for adsbdb origin/destination lookups.
FLIGHT_ROUTE_CACHE_PATH = (
    Path(cfg.jsonl_path).parent / "flight_routes.json.gz" if cfg.jsonl_path else None
)

adsbdb = AdsbdbClient(cache_path=FLIGHT_ROUTE_CACHE_PATH, enabled=cfg.flight_routes_enabled)

# Planespotters.net provides higher-quality community photographs with
# hotlinking permitted via their public API. We use them as the primary
# photo source and fall back to adsbdb's airport-data.com URL when
# planespotters has nothing for a given tail.
PHOTOS_CACHE_PATH = Path(cfg.jsonl_path).parent / "photos.json.gz" if cfg.jsonl_path else None
planespotters = PlanespottersClient(
    cache_path=PHOTOS_CACHE_PATH,
    enabled=cfg.flight_routes_enabled,
)

METAR_CACHE_PATH = Path(cfg.jsonl_path).parent / "metar.json.gz" if cfg.jsonl_path else None
metar = MetarClient(cache_path=METAR_CACHE_PATH, enabled=cfg.metar_enabled)

# Server-side watchlist + notification fan-out. The browser still owns
# the watchlist UI (mirrored to /api/watchlist) and now also the
# notifications channel list (mirrored to /api/notifications/config).
# Server alerts fire via whatever Telegram / ntfy / webhook entries
# the user saved, so they still get pinged with no tab open.
WATCHLIST_PATH = Path(cfg.jsonl_path).parent / "watchlist.json" if cfg.jsonl_path else None
watchlist_store = WatchlistStore(path=WATCHLIST_PATH)

NOTIFICATIONS_CONFIG_PATH = (
    Path(cfg.jsonl_path).parent / "notifications.json" if cfg.jsonl_path else None
)
notifications_config = NotificationsConfigStore(path=NOTIFICATIONS_CONFIG_PATH)
notifier = NotifierDispatcher(notifications_config)
alerts = AlertWatcher(watchlist_store, notifier)
if notifier.enabled:
    log.info("notifications wired: %s", ", ".join(notifier.configured_summary()))

COVERAGE_CACHE_PATH = Path(cfg.jsonl_path).parent / "coverage.json" if cfg.jsonl_path else None
# Polar coverage uses the TRUE receiver coordinates (before RECEIVER_ANON_KM
# snapping) so the max-range-per-bearing map reflects actual reception.
coverage = PolarCoverage(
    receiver_lat=cfg.lat_ref,
    receiver_lon=cfg.lon_ref,
    cache_path=COVERAGE_CACHE_PATH,
)

HEATMAP_CACHE_PATH = Path(cfg.jsonl_path).parent / "heatmap.json" if cfg.jsonl_path else None
heatmap = TrafficHeatmap(cache_path=HEATMAP_CACHE_PATH)
# Every time the registry creates a fresh Aircraft, bump the heatmap's
# (weekday, hour) bucket for the first-seen timestamp.
registry.on_new_aircraft = lambda _icao, ts: heatmap.observe(ts)

POLAR_HEATMAP_CACHE_PATH = (
    Path(cfg.jsonl_path).parent / "polar_heatmap.json" if cfg.jsonl_path else None
)
polar_heatmap = PolarHeatmap(
    receiver_lat=cfg.lat_ref,
    receiver_lon=cfg.lon_ref,
    cache_path=POLAR_HEATMAP_CACHE_PATH,
)

# VFRMap chart cycle — scraped from vfrmap.com at startup, cached to disk,
# refreshed in the background so the IFR Low / IFR High tile URLs stay
# pinned to the current 28-day FAA cycle without operator input. The
# env-var override is the escape hatch for air-gapped deployments and
# for reproducing bugs against a specific historical cycle.
VFRMAP_CYCLE_CACHE_PATH = (
    Path(cfg.jsonl_path).parent / "vfrmap_cycle.json" if cfg.jsonl_path else None
)
vfrmap_cycle = VfrmapCycle(
    cache_path=VFRMAP_CYCLE_CACHE_PATH,
    override=cfg.vfrmap_chart_date,
)
vfrmap_cycle.load_cache()


# Every accepted position fix feeds both the polar-coverage tracker (max
# range per bearing) and the polar heatmap (count per bearing x distance
# cell). Both are cheap in-process aggregations.
def _on_position(lat: float, lon: float) -> None:
    coverage.observe(lat, lon)
    polar_heatmap.observe(lat, lon)


registry.on_position = _on_position

# Short ring of one-shot events that need to surface on the live UI
# without living on any aircraft (e.g. "new polar-coverage record").
# `build_snapshot` drains into `snap["events"]` every tick, so events
# queued between ticks fan out once and vanish. Bounded so a
# disconnected client can't make the list grow unbounded.
_EGG_EVENT_CAP = 20
_pending_egg_events: list[dict[str, object]] = []


def _record_range_event(angle: float, dist_km: float) -> None:
    _pending_egg_events.append(
        {"type": "range_record", "angle": round(angle, 1), "dist_km": round(dist_km, 1)}
    )
    if len(_pending_egg_events) > _EGG_EVENT_CAP:
        # Drop oldest first — newer records are more interesting.
        del _pending_egg_events[: len(_pending_egg_events) - _EGG_EVENT_CAP]


coverage.on_new_max = _record_range_event


async def aircraft_db_refresher():
    """Periodically re-download the aircraft DB. Disabled when interval is 0."""
    if cfg.aircraft_db_refresh_hours <= 0 or AIRCRAFT_DB_PATH is None:
        return
    interval = cfg.aircraft_db_refresh_hours * 3600
    while True:
        try:
            await asyncio.sleep(interval)
            await asyncio.to_thread(
                aircraft_db.refresh_from_url, DEFAULT_REFRESH_URL, AIRCRAFT_DB_PATH
            )
        except asyncio.CancelledError:
            raise
        except Exception as e:
            log.warning("aircraft DB auto-refresh failed: %s", e)


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

    # Load the aircraft + airports DBs off the event loop (large files, pure CPU).
    db_task = asyncio.create_task(
        asyncio.to_thread(aircraft_db.load_first_available),
        name="aircraft_db_loader",
    )
    airports_task = asyncio.create_task(
        asyncio.to_thread(airports_db.load_first_available),
        name="airports_db_loader",
    )
    navaids_task = asyncio.create_task(
        asyncio.to_thread(navaids_db.load_first_available),
        name="navaids_db_loader",
    )
    airlines_task = asyncio.create_task(
        asyncio.to_thread(airlines_db.load_first_available),
        name="airlines_db_loader",
    )
    consumer = asyncio.create_task(beast_consumer(), name="beast_consumer")
    pusher = asyncio.create_task(snapshot_pusher(), name="snapshot_pusher")
    persister = asyncio.create_task(state_persister(), name="state_persister")
    refresher = asyncio.create_task(aircraft_db_refresher(), name="aircraft_db_refresher")
    # VFRMap chart cycle — one-shot discover on boot, then refresh every
    # few hours so the IFR tile URLs track the current 28-day FAA cycle.
    vfrmap_discover = asyncio.create_task(vfrmap_cycle.discover(), name="vfrmap_discover")
    vfrmap_refresher = asyncio.create_task(vfrmap_cycle.refresher(), name="vfrmap_refresher")
    try:
        yield
    finally:
        for t in (
            consumer,
            pusher,
            db_task,
            airports_task,
            navaids_task,
            airlines_task,
            persister,
            refresher,
            vfrmap_discover,
            vfrmap_refresher,
        ):
            t.cancel()
        await asyncio.gather(
            consumer,
            pusher,
            db_task,
            airports_task,
            navaids_task,
            airlines_task,
            persister,
            refresher,
            vfrmap_discover,
            vfrmap_refresher,
            return_exceptions=True,
        )
        # One last save so we don't lose the gap between the final periodic
        # write and shutdown.
        if STATE_PATH is not None:
            try:
                save_state(registry, STATE_PATH)
            except Exception as e:
                log.warning("final state save failed: %s", e)
        # Flush any pending watchlist last-seen updates (record_seen
        # debounces disk writes; we don't want to lose up to 30 s of
        # data on graceful shutdown).
        with contextlib.suppress(Exception):
            watchlist_store.flush()
        # Release pooled connections held by the HTTP clients.
        for closer in (
            adsbdb.aclose(),
            planespotters.aclose(),
            metar.aclose(),
            notifier.aclose(),
            vfrmap_cycle.aclose(),
        ):
            with contextlib.suppress(Exception):
                await closer
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


# Defence-in-depth headers on every HTTP response. Flightjar is self-
# hosted so a same-origin policy fits well, but it loads a handful of
# third-party resources (Leaflet from unpkg, OSM/CARTO/Esri tiles, adsbdb
# photo thumbnails from airport-data.com, country flags from flagcdn.com)
# that need to be whitelisted. `style-src 'unsafe-inline'` is kept because
# Leaflet and our altitude-legend both set element.style.* at runtime;
# hash-based CSP would be much noisier for little practical benefit.
_CSP = (
    "default-src 'self'; "
    "script-src 'self' https://unpkg.com; "
    "style-src 'self' https://unpkg.com 'unsafe-inline'; "
    "img-src 'self' data: blob: https:; "
    # unpkg appears in connect-src too so Leaflet's sourcemap fetch
    # (/leaflet.js.map) succeeds when devtools is open.
    "connect-src 'self' ws: wss: https://unpkg.com; "
    "font-src 'self'; "
    "frame-ancestors 'none'; "
    "base-uri 'self'; "
    "form-action 'self'"
)


@app.middleware("http")
async def security_headers(request, call_next):
    response = await call_next(request)
    response.headers.setdefault("X-Content-Type-Options", "nosniff")
    response.headers.setdefault("X-Frame-Options", "DENY")
    response.headers.setdefault("Referrer-Policy", "strict-origin-when-cross-origin")
    response.headers.setdefault("Permissions-Policy", "geolocation=(), microphone=(), camera=()")
    response.headers.setdefault("Content-Security-Policy", _CSP)
    return response


STATIC_DIR = Path(__file__).parent / "static"


class RevalidatingStaticFiles(StaticFiles):
    """StaticFiles that forces browsers to revalidate via ETag every request.

    The bundle is split across several ES modules (app.js imports format.js,
    units.js, …). Only the top-level app.js URL carries a content-hash query
    string, so without explicit cache headers Safari applies its heuristic
    freshness rule and serves submodule imports from local cache even after a
    deploy. The result: a new app.js that imports a newly-exported symbol
    fails to load against the old cached submodule, and the whole page
    silently breaks. `no-cache` keeps the entry in cache but forces a
    conditional GET on every request — the server returns 304 when the ETag
    still matches, so the bandwidth cost is a handful of bytes per file.
    """

    async def get_response(self, path, scope):
        response = await super().get_response(path, scope)
        response.headers["Cache-Control"] = "no-cache"
        return response


app.mount("/static", RevalidatingStaticFiles(directory=STATIC_DIR), name="static")


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
    return JSONResponse(build_snapshot())


@app.get("/api/airports", summary="Airports in a bounding box")
async def api_airports(
    min_lat: float,
    min_lon: float,
    max_lat: float,
    max_lon: float,
    limit: int = 2000,
):
    """Airports inside the requested bbox, biggest first.

    Drives the optional Airports layer on the map. Server holds the full
    OurAirports dataset (~30k entries); the client refetches on pan/zoom
    and only renders what's in view. `limit` caps the response so very
    wide views still return in bounded time.
    """
    # Latitudes must be ordered and within [-90, 90]; longitudes must each
    # be within [-180, 180] but may be inverted to wrap the antimeridian.
    if not (-90.0 <= min_lat <= max_lat <= 90.0):
        return JSONResponse({"error": "bad latitude bounds"}, status_code=400)
    if not (-180.0 <= min_lon <= 180.0 and -180.0 <= max_lon <= 180.0):
        return JSONResponse({"error": "bad longitude bounds"}, status_code=400)
    limit = max(1, min(int(limit), 5000))
    return airports_db.bbox(min_lat, min_lon, max_lat, max_lon, limit)


@app.get("/api/navaids", summary="Navaids (VOR / DME / NDB) in a bounding box")
async def api_navaids(
    min_lat: float,
    min_lon: float,
    max_lat: float,
    max_lon: float,
    limit: int = 2000,
):
    """Navaids inside the requested bbox, VOR family first.

    Drives the optional Navaids layer on the map. Server holds the full
    OurAirports navaids dataset (~12k entries); the client refetches on
    pan/zoom and only renders what's in view. `limit` caps the response
    so very wide views still return in bounded time.
    """
    if not (-90.0 <= min_lat <= max_lat <= 90.0):
        return JSONResponse({"error": "bad latitude bounds"}, status_code=400)
    if not (-180.0 <= min_lon <= 180.0 and -180.0 <= max_lon <= 180.0):
        return JSONResponse({"error": "bad longitude bounds"}, status_code=400)
    limit = max(1, min(int(limit), 5000))
    return navaids_db.bbox(min_lat, min_lon, max_lat, max_lon, limit)


@app.get("/api/map_config", summary="Client-side map config (tile keys, chart cycles)")
async def api_map_config():
    """Configuration the browser needs before it can register tile overlays.

    Keeps deploy-time secrets (OpenAIP API key) out of the static
    `index.html` and surfaces the auto-discovered VFRMap chart cycle so
    the client doesn't have to re-scrape it. All fields are strings —
    empty means "unset, don't register the overlay".
    """
    return {
        "openaip_api_key": cfg.openaip_api_key,
        "vfrmap_chart_date": vfrmap_cycle.current_date() or "",
    }


@app.get("/api/coverage", summary="Polar coverage map (max distance per bearing)")
async def api_coverage():
    """Return the receiver's observed polar coverage.

    Built up over the runtime from every decoded position; persisted to
    /data/coverage.json so restarts don't lose it. Response shape:

        {
          "receiver": {"lat": .., "lon": ..},
          "bucket_deg": 10.0,
          "bearings": [
            {"angle": 5.0,  "dist_km": 42.1},
            {"angle": 15.0, "dist_km": 48.3},
            ...
          ]
        }

    Bearings with no observations are omitted so the frontend can draw a
    single polygon through the populated sectors.
    """
    return coverage.snapshot()


@app.post("/api/coverage/reset", summary="Clear the polar coverage map")
async def api_coverage_reset():
    """Reset all bearing buckets to zero — useful after moving antennas."""
    coverage.reset()
    return {"ok": True}


@app.get("/api/heatmap", summary="New-aircraft counts by weekday x hour")
async def api_heatmap():
    """Return a 7x24 traffic grid plus marginal totals.

    Bumped once per new Aircraft record the registry creates, so a
    tail that pops in and out of coverage within a session is counted
    once. Kept on disk in /data/heatmap.json so history survives
    restarts; POST /api/heatmap/reset wipes it.
    """
    return heatmap.snapshot()


@app.post("/api/heatmap/reset", summary="Clear the traffic heatmap")
async def api_heatmap_reset():
    heatmap.reset()
    return {"ok": True}


@app.get("/api/watchlist", summary="Server-side watchlist of ICAO24 codes")
async def api_watchlist():
    """Return the current watchlist plus last-seen timestamps. Shape:

        {
          "icao24s": ["abc123", ...],
          "last_seen": {"abc123": 1714000000.0, ...}
        }

    The client merges `icao24s` with its local copy on page load and
    uses `last_seen` to render "last seen Xh ago" rows in the
    watchlist dialog for tails that aren't currently in coverage."""
    return watchlist_store.get()


@app.post("/api/watchlist", summary="Replace the watchlist")
async def api_watchlist_replace(body: dict):
    """Overwrite the watchlist with the supplied list of ICAO24 hex
    codes. Invalid entries are dropped silently; `icao24s` is required
    (an empty list wipes the watchlist). Last-seen entries for removed
    tails are pruned automatically."""
    if not isinstance(body, dict) or not isinstance(body.get("icao24s"), list):
        return JSONResponse({"error": 'body must be {"icao24s": [...]}'}, status_code=400)
    # Cap body size to keep a rogue client from blowing memory.
    if len(body["icao24s"]) > 10_000:
        return JSONResponse({"error": "too many entries"}, status_code=413)
    return watchlist_store.replace(body["icao24s"])


@app.get("/api/notifications/config", summary="Notification channel config")
async def api_notifications_config():
    """Return the stored list of notification channels so the UI can
    populate its settings dialog. Sensitive fields (Telegram bot
    tokens, ntfy auth tokens) come through in plain text — the dialog
    masks them client-side."""
    return notifications_config.get()


@app.post("/api/notifications/config", summary="Replace notification channels")
async def api_notifications_config_replace(body: dict):
    """Overwrite the channel list from a POST body of shape
    `{channels: [...]}`. Unknown types / unknown fields are stripped
    server-side; each channel gets a stable ID (preserved across saves
    unless the client sends a new one)."""
    if not isinstance(body, dict):
        return JSONResponse({"error": "body must be an object"}, status_code=400)
    if len(body.get("channels") or []) > 100:
        return JSONResponse({"error": "too many channels"}, status_code=413)
    return notifications_config.replace(body)


@app.post(
    "/api/notifications/test/{channel_id}",
    summary="Send a test alert through one channel",
)
async def api_notifications_test(channel_id: str):
    """Fire a one-off test message via `channel_id`. The UI exposes
    this as a Test button so users can confirm a token / URL works
    without waiting for a live event."""
    ok = await notifier.test_channel(channel_id)
    if not ok:
        return JSONResponse({"error": "channel not found or not configured"}, status_code=404)
    return {"ok": True}


@app.get("/api/flight/{callsign}", summary="Origin / destination for a callsign")
async def api_flight(callsign: str):
    """Lookup origin + destination airports for a flight callsign.

    Uses adsbdb.com's `/v0/callsign/<callsign>` endpoint, cached server-side
    for 12h (positive) / 1h (negative). Returns a null payload when the
    feature is disabled or the callsign is unknown.
    """
    cs = callsign.strip().upper()
    if not cs or len(cs) > 8 or not all(c.isalnum() for c in cs):
        return JSONResponse({"callsign": callsign, "error": "bad callsign"}, status_code=400)
    if not adsbdb.enabled:
        return {"callsign": cs, "origin": None, "destination": None}
    data = await adsbdb.lookup_route(cs)
    if data is None:
        return {"callsign": cs, "origin": None, "destination": None}
    return {"callsign": cs, **data}


@app.get("/api/aircraft/{icao24}", summary="Per-tail details for an aircraft")
async def api_aircraft_info(icao24: str):
    """Lookup per-tail details (registration, type, owner, photo URLs) for
    this Mode-S ICAO24.

    Metadata comes from adsbdb.com's `/v0/aircraft/<hex>` endpoint.
    Photos prefer planespotters.net (higher quality, photographer
    credit surfaced) and fall back to adsbdb's airport-data.com URL
    when planespotters has nothing. Both caches are on disk (30 d
    positive / 24 h negative). Photo URLs are hotlinked — the browser
    fetches them direct from the CDN without round-tripping this
    server.
    """
    icao = icao24.strip().lower()
    if not icao or len(icao) > 6 or not all(c in "0123456789abcdef" for c in icao):
        return JSONResponse({"icao": icao24, "error": "bad icao"}, status_code=400)
    if not adsbdb.enabled:
        return {"icao": icao}
    data = await adsbdb.lookup_aircraft(icao)
    if data is None:
        return {"icao": icao}

    # Try to upgrade the photo to planespotters when we have a tail to
    # query by. If planespotters has nothing, keep adsbdb's fields.
    photo_thumbnail = data.get("photo_thumbnail")
    photo_url = data.get("photo_url")
    photo_credit = None
    reg = data.get("registration")
    if reg:
        ps = await planespotters.lookup(reg)
        if ps:
            photo_thumbnail = ps.get("thumbnail") or photo_thumbnail
            photo_url = ps.get("link") or ps.get("large") or photo_url
            photo_credit = ps.get("photographer")
    return {
        "icao": icao,
        **data,
        "photo_thumbnail": photo_thumbnail,
        "photo_url": photo_url,
        "photo_credit": photo_credit,
    }


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
        # Baked in at docker build time via the FLIGHTJAR_VERSION env
        # var (defaults to "dev" for local builds).
        "version": os.environ.get("FLIGHTJAR_VERSION", "dev"),
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
        await websocket.send_text(json.dumps(build_snapshot(), separators=(",", ":")))
        while True:
            # drain any client messages (we don't expect any, but keep alive)
            await websocket.receive_text()
    except WebSocketDisconnect:
        pass
    except Exception as e:
        log.info("ws error: %s", e)
    finally:
        broadcaster.remove(websocket)
