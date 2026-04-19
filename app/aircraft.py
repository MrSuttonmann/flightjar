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
TRAIL_MAX_POINTS = 120  # ~2 minutes at typical 1Hz position rate
AIRCRAFT_TIMEOUT = 60.0  # drop from registry after this many seconds idle

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

    @property
    def altitude(self) -> int | None:
        """Best-known altitude: prefer barometric, fall back to GNSS."""
        return self.altitude_baro if self.altitude_baro is not None else self.altitude_geo


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
    ) -> bool:
        """Update state from one Mode S message. Returns True if accepted."""
        if now is None:
            now = time.time()
        r = _decode(hex_msg)
        df = r.get("df")
        if df is None:
            return False
        if df in (17, 18):
            return self._ingest_adsb(r, hex_msg, now, mlat_ticks)
        if df in (4, 5, 11, 20, 21):
            return self._ingest_surveillance(r, now, mlat_ticks)
        return False

    def _get(self, icao: str) -> Aircraft:
        ac = self.aircraft.get(icao)
        if ac is None:
            ac = Aircraft(icao=icao)
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

        ac.lat = new_lat
        ac.lon = new_lon
        ac.last_position_time = now
        ac.trail.append((round(new_lat, 5), round(new_lon, 5), ac.altitude, now))

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
            distance_km = None
            if (
                ref_lat is not None
                and ref_lon is not None
                and ac.lat is not None
                and ac.lon is not None
            ):
                dlat = math.radians(ac.lat - ref_lat)
                dlon = math.radians(ac.lon - ref_lon) * math.cos(math.radians(ref_lat))
                distance_km = round(math.hypot(dlat, dlon) * 6371, 2)
            if ac.lat is not None:
                positioned += 1
            info = self.aircraft_db.lookup(ac.icao) if self.aircraft_db else None
            out.append(
                {
                    "icao": ac.icao,
                    "callsign": ac.callsign,
                    "registration": info.get("registration") if info else None,
                    "type_icao": info.get("type_icao") if info else None,
                    "type_long": info.get("type_long") if info else None,
                    "lat": ac.lat,
                    "lon": ac.lon,
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
                    "age": round(now - ac.last_seen, 1),
                    "msg_count": ac.msg_count,
                    "distance_km": distance_km,
                    "trail": [[lat, lon, alt] for lat, lon, alt, _ in ac.trail],
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
