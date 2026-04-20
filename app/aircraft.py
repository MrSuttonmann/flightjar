"""Per-aircraft state, fed by Mode S messages.

Position decoding uses pyModeS 3.x's unified `decode()`:
  * Global decode requires an even+odd CPR pair within ~10s.
  * Once we have a position (or if a receiver lat/lon is configured) we can
    do local decode from a single message, which is faster to first fix.
"""

import contextlib
import logging
import math
import time
from collections import deque
from dataclasses import dataclass, field
from typing import Any

import pyModeS as pms

from .aircraft_db import AircraftDB

log = logging.getLogger("beast.aircraft")

POSITION_PAIR_MAX_AGE = 10.0  # seconds; CPR global decode validity window
TRAIL_MAX_POINTS = 300  # ~5 minutes at typical 1Hz position rate
AIRCRAFT_TIMEOUT = 60.0  # drop from registry after this many seconds idle
PERSIST_MAX_AGE = 600.0  # restored aircraft older than this are discarded
# Dead-reckoning window. When a position is older than
# DEAD_RECKON_MIN_AGE we extrapolate along the last known track at the
# last known groundspeed so the plane keeps moving smoothly between
# reception gaps. Past DEAD_RECKON_MAX_AGE we freeze at the last real
# fix — straight-line extrapolation over a minute of silence is as
# likely to mislead as to help.
DEAD_RECKON_MIN_AGE = 1.5
DEAD_RECKON_MAX_AGE = 30.0
# When a real fix resumes after dead-reckoning and it's more than this far
# from where we'd extrapolated to, treat the extrapolation as misleading:
# clear the trail so the next coloured segment starts from the new fix
# rather than drawing a diagonal straight across the gap.
DEAD_RECKON_RESUME_RESET_KM = 5.0
EARTH_KM = 6371.0

EMERGENCY_SQUAWKS = {
    "7500": "hijack",
    "7600": "radio",
    "7700": "general",
}


@dataclass
class Aircraft:
    icao: str
    callsign: str | None = None
    category: int | None = None

    lat: float | None = None
    lon: float | None = None
    altitude_baro: int | None = None  # feet, barometric (DF17 TC 9-18, DF4/20)
    altitude_geo: int | None = None  # feet, GNSS / geometric (DF17 TC 20-22)
    track: float | None = None  # degrees, 0 = north
    speed: float | None = None  # knots (ground speed)
    vrate: int | None = None  # ft/min
    squawk: str | None = None
    on_ground: bool = False

    last_seen: float = 0.0
    last_position_time: float = 0.0
    first_seen: float = 0.0
    # Most recent BEAST MLAT tick stamp; a per-receiver 12 MHz counter.
    # Useful for sub-second spacing; don't compare across receivers.
    last_seen_mlat: int | None = None

    # CPR pair state for global airborne decode
    even_msg: str | None = None
    even_t: float = 0.0
    odd_msg: str | None = None
    odd_t: float = 0.0

    trail: deque = field(default_factory=lambda: deque(maxlen=TRAIL_MAX_POINTS))
    msg_count: int = 0
    # Peak BEAST signal byte (0-255) seen for this aircraft. dump1090/readsb
    # pack the per-message signal level into a single byte; we keep the max
    # so the panel can show "best reception seen" rather than a jittery
    # per-frame value.
    signal_peak: int | None = None

    @property
    def altitude(self) -> int | None:
        """Best-known altitude: prefer barometric, fall back to GNSS."""
        return self.altitude_baro if self.altitude_baro is not None else self.altitude_geo


def _approx_distance_km(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    """Equirectangular approximation — accurate enough for the <20 km
    corrections we care about here, and cheaper than haversine."""
    dlat = math.radians(lat2 - lat1)
    dlon = math.radians(lon2 - lon1) * math.cos(math.radians((lat1 + lat2) / 2))
    return math.hypot(dlat, dlon) * EARTH_KM


def _dead_reckon(
    lat: float, lon: float, track_deg: float, speed_kn: float, elapsed_s: float
) -> tuple[float, float]:
    """Project a position along a great-circle track at groundspeed.

    All inputs in the canonical wire units: lat/lon in degrees, track in
    degrees (0=N, 90=E), groundspeed in knots, elapsed in seconds.
    """
    dist_km = (speed_kn * 1.852) * (elapsed_s / 3600.0)
    if dist_km <= 0:
        return lat, lon
    phi1 = math.radians(lat)
    lam1 = math.radians(lon)
    theta = math.radians(track_deg)
    d = dist_km / EARTH_KM
    phi2 = math.asin(math.sin(phi1) * math.cos(d) + math.cos(phi1) * math.sin(d) * math.cos(theta))
    lam2 = lam1 + math.atan2(
        math.sin(theta) * math.sin(d) * math.cos(phi1),
        math.cos(d) - math.sin(phi1) * math.sin(phi2),
    )
    return math.degrees(phi2), ((math.degrees(lam2) + 540) % 360) - 180


def _decode(msg, **kw) -> dict:
    """pms.decode() with swallowed exceptions, always returning a dict-like."""
    try:
        r = pms.decode(msg, **kw)
    except Exception as e:
        log.debug("decode error: %s", e)
        return {}
    if not r:
        return {}
    # pms.decode returns a Decoded dict subclass; treat it as a plain mapping.
    if isinstance(r, list):
        return dict(r[-1]) if r else {}
    return dict(r)


class AircraftRegistry:
    def __init__(
        self,
        lat_ref: float | None = None,
        lon_ref: float | None = None,
        receiver_info: dict | None = None,
        site_name: str | None = None,
        aircraft_db: "AircraftDB | None" = None,
    ):
        self.aircraft: dict[str, Aircraft] = {}
        self.lat_ref = lat_ref
        self.lon_ref = lon_ref
        # Info shown to clients; may be anonymised relative to lat_ref/lon_ref.
        self.receiver_info = receiver_info
        self.site_name = site_name
        self.aircraft_db = aircraft_db

    # -------- ingest --------

    def ingest(
        self,
        hex_msg: str,
        now: float | None = None,
        mlat_ticks: int | None = None,
        signal: int | None = None,
    ) -> bool:
        """Update state from one Mode S message. Returns True if accepted."""
        if now is None:
            now = time.time()
        r = _decode(hex_msg)
        df = r.get("df")
        if df is None:
            return False
        if df in (17, 18):
            accepted = self._ingest_adsb(r, hex_msg, now, mlat_ticks)
        elif df in (4, 5, 11, 20, 21):
            accepted = self._ingest_surveillance(r, now, mlat_ticks)
        else:
            return False
        if accepted and signal is not None:
            icao = r.get("icao")
            if icao:
                ac = self.aircraft.get(icao)
                if ac is not None and (ac.signal_peak is None or signal > ac.signal_peak):
                    ac.signal_peak = signal
        return accepted

    def _get(self, icao: str) -> Aircraft:
        ac = self.aircraft.get(icao)
        if ac is None:
            ac = Aircraft(icao=icao, first_seen=time.time())
            self.aircraft[icao] = ac
        return ac

    def _ingest_adsb(self, r: dict, msg: str, now: float, mlat_ticks: int | None = None) -> bool:
        if not r.get("crc_valid"):
            return False
        icao = r.get("icao")
        tc = r.get("typecode")
        if not icao or tc is None:
            return False

        ac = self._get(icao)
        ac.last_seen = now
        if mlat_ticks is not None:
            ac.last_seen_mlat = mlat_ticks
        ac.msg_count += 1

        if 1 <= tc <= 4:
            cs = r.get("callsign")
            if cs:
                ac.callsign = str(cs).rstrip("_ ").strip() or None
            cat = r.get("category")
            if cat is not None:
                ac.category = cat

        elif 5 <= tc <= 8:
            # Surface position
            ac.on_ground = True
            self._update_position(ac, msg, now, r, surface=True)

        elif 9 <= tc <= 18:
            # Airborne baro altitude. Set first so the trail point captures it.
            ac.on_ground = False
            alt = r.get("altitude")
            if alt is not None:
                with contextlib.suppress(TypeError, ValueError):
                    ac.altitude_baro = int(alt)
            self._update_position(ac, msg, now, r, surface=False)

        elif 20 <= tc <= 22:
            # Airborne GNSS (geometric) altitude.
            ac.on_ground = False
            alt = r.get("altitude")
            if alt is not None:
                with contextlib.suppress(TypeError, ValueError):
                    ac.altitude_geo = int(alt)
            self._update_position(ac, msg, now, r, surface=False)

        elif tc == 19:
            # Velocity. Subtypes 1/2 give groundspeed+track; 3/4 give airspeed+heading.
            spd = r.get("groundspeed")
            if spd is None:
                spd = r.get("airspeed")
            if spd is not None:
                ac.speed = float(spd)
            trk = r.get("track")
            if trk is None:
                trk = r.get("heading")
            if trk is not None:
                ac.track = float(trk)
            vr = r.get("vertical_rate")
            if vr is not None:
                with contextlib.suppress(TypeError, ValueError):
                    ac.vrate = int(vr)

        return True

    def _ingest_surveillance(self, r: dict, now: float, mlat_ticks: int | None = None) -> bool:
        """Handle DF 4/20 (altitude), DF 5/21 (squawk), DF 11 (all-call)."""
        icao = r.get("icao")
        if not icao:
            return False
        ac = self._get(icao)
        ac.last_seen = now
        if mlat_ticks is not None:
            ac.last_seen_mlat = mlat_ticks
        ac.msg_count += 1
        alt = r.get("altitude")
        if alt is not None:
            # Surveillance altcode is always barometric.
            with contextlib.suppress(TypeError, ValueError):
                ac.altitude_baro = int(alt)
        sq = r.get("squawk")
        if sq is not None:
            ac.squawk = str(sq)
        return True

    # -------- position decoding --------

    def _update_position(self, ac: Aircraft, msg: str, now: float, result: dict, surface: bool):
        oe = result.get("cpr_format")
        if oe == 0:
            ac.even_msg, ac.even_t = msg, now
        elif oe == 1:
            ac.odd_msg, ac.odd_t = msg, now

        pos = None

        # 1. Global decode if we have a fresh pair.
        #    Surface global decode needs a reference point; skip if unset.
        pair_fresh = (
            ac.even_msg and ac.odd_msg and abs(ac.even_t - ac.odd_t) < POSITION_PAIR_MAX_AGE
        )
        if pair_fresh and (not surface or self.lat_ref is not None):
            assert ac.even_msg is not None and ac.odd_msg is not None
            try:
                msgs: list[str] = [ac.even_msg, ac.odd_msg]
                ts = [ac.even_t, ac.odd_t]
                if surface:
                    assert self.lat_ref is not None and self.lon_ref is not None
                    batch = pms.decode(
                        msgs,
                        timestamps=ts,
                        surface_ref=(self.lat_ref, self.lon_ref),
                    )
                else:
                    batch = pms.decode(msgs, timestamps=ts)
                latest = batch[-1] if isinstance(batch, list) else batch
                lat = latest.get("latitude") if latest else None
                lon = latest.get("longitude") if latest else None
                if lat is not None and lon is not None:
                    pos = (lat, lon)
            except Exception as e:
                log.debug("global cpr fail %s: %s", ac.icao, e)

        # 2. Local decode using last known position for this aircraft
        if pos is None and ac.lat is not None and ac.lon is not None:
            ref = (ac.lat, ac.lon)
            r2 = _decode(msg, surface_ref=ref) if surface else _decode(msg, reference=ref)
            lat, lon = r2.get("latitude"), r2.get("longitude")
            if lat is not None and lon is not None:
                pos = (lat, lon)

        # 3. Local decode using configured receiver reference
        if pos is None and self.lat_ref is not None and self.lon_ref is not None:
            ref = (self.lat_ref, self.lon_ref)
            r2 = _decode(msg, surface_ref=ref) if surface else _decode(msg, reference=ref)
            lat, lon = r2.get("latitude"), r2.get("longitude")
            if lat is not None and lon is not None:
                pos = (lat, lon)

        if pos is None:
            return
        new_lat, new_lon = pos
        if not (-90 <= new_lat <= 90 and -180 <= new_lon <= 180):
            return

        # Sanity check: reject teleports (>500 nm from previous fix)
        if ac.lat is not None:
            dlat = abs(new_lat - ac.lat)
            dlon = abs(new_lon - ac.lon)
            if dlat > 8 or dlon > 8:
                log.debug(
                    "rejecting teleport %s: %.3f,%.3f -> %.3f,%.3f",
                    ac.icao,
                    ac.lat,
                    ac.lon,
                    new_lat,
                    new_lon,
                )
                return

        # Dead-reckoning correction check: if the plane went silent long
        # enough that the frontend had been extrapolating, compare the new
        # fix to where extrapolation would have placed it. A big delta
        # (turn during the gap, missed altitude change, etc.) means the
        # straight-line dashed dead-reckoning line was misleading — clear
        # the trail so the next coloured segment starts from this fix
        # instead of drawing a long diagonal across the gap.
        if (
            not surface
            and ac.lat is not None
            and ac.lon is not None
            and ac.last_position_time > 0
            and ac.speed is not None
            and ac.track is not None
        ):
            elapsed = now - ac.last_position_time
            if DEAD_RECKON_MIN_AGE < elapsed <= DEAD_RECKON_MAX_AGE:
                pred_lat, pred_lon = _dead_reckon(ac.lat, ac.lon, ac.track, ac.speed, elapsed)
                error_km = _approx_distance_km(pred_lat, pred_lon, new_lat, new_lon)
                if error_km > DEAD_RECKON_RESUME_RESET_KM:
                    log.info(
                        "dead-reckon miss %s: %.1f km off after %.1fs — clearing trail",
                        ac.icao,
                        error_km,
                        elapsed,
                    )
                    ac.trail.clear()

        ac.lat = new_lat
        ac.lon = new_lon
        ac.last_position_time = now
        # Trail point shape: (lat, lon, altitude, speed, timestamp). Speed
        # lets the frontend draw a speed sparkline alongside the altitude
        # one; we snapshot ac.speed at the time of the position fix so
        # the two traces are always time-aligned.
        ac.trail.append((round(new_lat, 5), round(new_lon, 5), ac.altitude, ac.speed, now))

    # -------- persistence --------

    # Fields we serialize. Intentionally small: CPR pair state and even/odd
    # messages don't need to survive restarts (next message will re-seed).
    _PERSIST_FIELDS = (
        "icao",
        "callsign",
        "category",
        "lat",
        "lon",
        "altitude_baro",
        "altitude_geo",
        "track",
        "speed",
        "vrate",
        "squawk",
        "on_ground",
        "last_seen",
        "last_position_time",
        "first_seen",
        "last_seen_mlat",
        "msg_count",
        "signal_peak",
    )

    def serialize(self) -> dict:
        """Return a JSON-friendly snapshot of the registry for persistence."""
        out = {}
        for icao, ac in self.aircraft.items():
            entry = {f: getattr(ac, f) for f in self._PERSIST_FIELDS}
            entry["trail"] = [list(p) for p in ac.trail]
            out[icao] = entry
        return {"version": 1, "saved_at": time.time(), "aircraft": out}

    def restore(self, data: dict, now: float | None = None) -> int:
        """Load registry state from a previous serialize() payload.

        Drops entries whose last_seen is older than PERSIST_MAX_AGE seconds
        relative to `now` (so a long-stopped container doesn't bring back
        aircraft that have clearly departed). Returns the count loaded.
        """
        if not isinstance(data, dict) or data.get("version") != 1:
            return 0
        if now is None:
            now = time.time()
        cutoff = now - PERSIST_MAX_AGE
        loaded = 0
        for icao, entry in (data.get("aircraft") or {}).items():
            try:
                if (entry.get("last_seen") or 0) < cutoff:
                    continue
                ac = Aircraft(icao=icao)
                for f in self._PERSIST_FIELDS:
                    if f in entry and f != "icao":
                        setattr(ac, f, entry[f])
                for p in entry.get("trail") or []:
                    # Accept both old 4-tuple (lat,lon,alt,ts) and new 5-tuple
                    # (lat,lon,alt,spd,ts) persisted states so a post-upgrade
                    # restart doesn't drop history.
                    if len(p) >= 5:
                        ac.trail.append(tuple(p[:5]))
                    elif len(p) == 4:
                        ac.trail.append((p[0], p[1], p[2], None, p[3]))
                self.aircraft[icao] = ac
                loaded += 1
            except Exception as e:
                log.debug("skipping malformed persisted aircraft %s: %s", icao, e)
        return loaded

    # -------- output --------

    def cleanup(self, now: float):
        stale = [
            icao for icao, ac in self.aircraft.items() if now - ac.last_seen > AIRCRAFT_TIMEOUT
        ]
        for icao in stale:
            del self.aircraft[icao]

    def snapshot(self, now: float | None = None) -> dict:
        if now is None:
            now = time.time()
        # Reference for distance_km is the displayed receiver position (which
        # may be anonymised), so the number matches what the UI renders.
        ref = self.receiver_info or {}
        ref_lat, ref_lon = ref.get("lat"), ref.get("lon")
        out: list[dict[str, Any]] = []
        positioned = 0
        for ac in self.aircraft.values():
            # Skip aircraft with no callsign AND no position/speed/altitude —
            # a lone squawk or ICAO isn't enough to be worth listing.
            if ac.callsign is None and ac.lat is None and ac.speed is None and ac.altitude is None:
                continue
            # Dead-reckon: if the last real fix is a few seconds stale but
            # we still have a track + groundspeed, project forward so the
            # marker slides smoothly between position reports instead of
            # freezing. Airborne only (surface decoding shouldn't cheat).
            disp_lat, disp_lon = ac.lat, ac.lon
            position_stale = False
            if (
                ac.lat is not None
                and ac.lon is not None
                and not ac.on_ground
                and ac.speed is not None
                and ac.track is not None
                and ac.last_position_time > 0
            ):
                age = now - ac.last_position_time
                if DEAD_RECKON_MIN_AGE < age <= DEAD_RECKON_MAX_AGE:
                    disp_lat, disp_lon = _dead_reckon(ac.lat, ac.lon, ac.track, ac.speed, age)
                    position_stale = True
            distance_km = None
            if (
                ref_lat is not None
                and ref_lon is not None
                and disp_lat is not None
                and disp_lon is not None
            ):
                dlat = math.radians(disp_lat - ref_lat)
                dlon = math.radians(disp_lon - ref_lon) * math.cos(math.radians(ref_lat))
                distance_km = round(math.hypot(dlat, dlon) * 6371, 2)
            if disp_lat is not None:
                positioned += 1
            info = self.aircraft_db.lookup(ac.icao) if self.aircraft_db else None
            out.append(
                {
                    "icao": ac.icao,
                    "callsign": ac.callsign,
                    "category": ac.category,
                    "registration": info.get("registration") if info else None,
                    "type_icao": info.get("type_icao") if info else None,
                    "type_long": info.get("type_long") if info else None,
                    "lat": disp_lat,
                    "lon": disp_lon,
                    "position_stale": position_stale,
                    "altitude": ac.altitude,
                    "altitude_baro": ac.altitude_baro,
                    "altitude_geo": ac.altitude_geo,
                    "track": ac.track,
                    "speed": ac.speed,
                    "vrate": ac.vrate,
                    "squawk": ac.squawk,
                    "emergency": EMERGENCY_SQUAWKS.get(ac.squawk) if ac.squawk else None,
                    "on_ground": ac.on_ground,
                    "last_seen": ac.last_seen,
                    "first_seen": ac.first_seen or None,
                    "signal_peak": ac.signal_peak,
                    "msg_count": ac.msg_count,
                    "distance_km": distance_km,
                    "trail": [[lat, lon, alt, spd] for lat, lon, alt, spd, _ in ac.trail],
                }
            )
        # Newest first
        out.sort(key=lambda a: float(a["last_seen"]), reverse=True)
        return {
            "now": now,
            "count": len(out),
            "positioned": positioned,
            "receiver": self.receiver_info,
            "site_name": self.site_name,
            "aircraft": out,
        }
