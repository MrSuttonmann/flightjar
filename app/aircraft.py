"""Per-aircraft state, fed by Mode S messages.

Position decoding uses pyModeS:
  * Global decode requires an even+odd CPR pair within ~10s.
  * Once we have a position (or if a receiver lat/lon is configured) we can
    do local decode from a single message, which is faster to first fix.
"""

import logging
import time
from collections import deque
from dataclasses import dataclass, field
from typing import Optional

import pyModeS as pms

log = logging.getLogger("beast.aircraft")

POSITION_PAIR_MAX_AGE = 10.0   # seconds; CPR global decode validity window
TRAIL_MAX_POINTS = 60          # ~1 minute at typical 1Hz position rate
AIRCRAFT_TIMEOUT = 60.0        # drop from registry after this many seconds idle


@dataclass
class Aircraft:
    icao: str
    callsign: Optional[str] = None
    category: Optional[int] = None

    lat: Optional[float] = None
    lon: Optional[float] = None
    altitude: Optional[int] = None     # feet
    track: Optional[float] = None      # degrees, 0 = north
    speed: Optional[float] = None      # knots (ground speed)
    vrate: Optional[int] = None        # ft/min
    squawk: Optional[str] = None
    on_ground: bool = False

    last_seen: float = 0.0
    last_position_time: float = 0.0

    # CPR pair state for global airborne decode
    even_msg: Optional[str] = None
    even_t: float = 0.0
    odd_msg: Optional[str] = None
    odd_t: float = 0.0

    trail: deque = field(default_factory=lambda: deque(maxlen=TRAIL_MAX_POINTS))
    msg_count: int = 0


class AircraftRegistry:
    def __init__(self, lat_ref: Optional[float] = None,
                 lon_ref: Optional[float] = None):
        self.aircraft: dict[str, Aircraft] = {}
        self.lat_ref = lat_ref
        self.lon_ref = lon_ref

    # -------- ingest --------

    def ingest(self, hex_msg: str, now: Optional[float] = None) -> bool:
        """Update state from one Mode S message. Returns True if accepted."""
        if now is None:
            now = time.time()
        try:
            df = pms.df(hex_msg)
        except Exception:
            return False

        if df in (17, 18):
            return self._ingest_adsb(hex_msg, now)
        if df in (4, 20):
            return self._ingest_altcode(hex_msg, now)
        if df in (5, 21):
            return self._ingest_idcode(hex_msg, now)
        if df == 11:
            return self._ingest_allcall(hex_msg, now)
        return False

    def _get(self, icao: str) -> Aircraft:
        ac = self.aircraft.get(icao)
        if ac is None:
            ac = Aircraft(icao=icao)
            self.aircraft[icao] = ac
        return ac

    def _ingest_adsb(self, msg: str, now: float) -> bool:
        try:
            if pms.crc(msg) != 0:
                return False
            icao = pms.adsb.icao(msg)
            tc = pms.adsb.typecode(msg)
        except Exception:
            return False
        if not icao:
            return False

        ac = self._get(icao)
        ac.last_seen = now
        ac.msg_count += 1

        try:
            if 1 <= tc <= 4:
                cs = pms.adsb.callsign(msg)
                if cs:
                    ac.callsign = cs.rstrip("_ ").strip() or None
                try:
                    ac.category = pms.adsb.category(msg)
                except Exception:
                    pass

            elif 5 <= tc <= 8:
                # Surface position
                ac.on_ground = True
                self._update_position(ac, msg, now, surface=True)

            elif 9 <= tc <= 18 or 20 <= tc <= 22:
                # Airborne position (baro alt for 9-18, GNSS alt for 20-22)
                ac.on_ground = False
                self._update_position(ac, msg, now, surface=False)
                try:
                    alt = pms.adsb.altitude(msg)
                    if alt is not None:
                        ac.altitude = int(alt)
                except Exception:
                    pass

            elif tc == 19:
                v = pms.adsb.velocity(msg)
                if v:
                    spd, hdg, vr, _vtype = v
                    if spd is not None:
                        ac.speed = float(spd)
                    if hdg is not None:
                        ac.track = float(hdg)
                    if vr is not None:
                        ac.vrate = int(vr)
        except Exception as e:
            log.debug("adsb decode error tc=%s: %s", tc, e)

        return True

    def _ingest_altcode(self, msg: str, now: float) -> bool:
        try:
            icao = pms.common.icao(msg)
        except Exception:
            return False
        if not icao:
            return False
        ac = self._get(icao)
        ac.last_seen = now
        ac.msg_count += 1
        try:
            alt = pms.common.altcode(msg)
            if alt is not None:
                ac.altitude = int(alt)
        except Exception:
            pass
        return True

    def _ingest_idcode(self, msg: str, now: float) -> bool:
        try:
            icao = pms.common.icao(msg)
        except Exception:
            return False
        if not icao:
            return False
        ac = self._get(icao)
        ac.last_seen = now
        ac.msg_count += 1
        try:
            sq = pms.common.idcode(msg)
            if sq is not None:
                ac.squawk = str(sq)
        except Exception:
            pass
        return True

    def _ingest_allcall(self, msg: str, now: float) -> bool:
        try:
            icao = pms.common.icao(msg)
        except Exception:
            return False
        if not icao:
            return False
        ac = self._get(icao)
        ac.last_seen = now
        ac.msg_count += 1
        return True

    # -------- position decoding --------

    def _update_position(self, ac: Aircraft, msg: str, now: float, surface: bool):
        try:
            oe = pms.adsb.oe_flag(msg)
        except Exception:
            return

        if oe == 0:
            ac.even_msg, ac.even_t = msg, now
        else:
            ac.odd_msg, ac.odd_t = msg, now

        pos = None

        # 1. Global decode if we have a fresh pair
        if (ac.even_msg and ac.odd_msg
                and abs(ac.even_t - ac.odd_t) < POSITION_PAIR_MAX_AGE):
            try:
                if surface:
                    if self.lat_ref is not None:
                        pos = pms.adsb.surface_position(
                            ac.even_msg, ac.odd_msg,
                            ac.even_t, ac.odd_t,
                            self.lat_ref, self.lon_ref,
                        )
                else:
                    pos = pms.adsb.airborne_position(
                        ac.even_msg, ac.odd_msg,
                        ac.even_t, ac.odd_t,
                    )
            except Exception as e:
                log.debug("global cpr fail %s: %s", ac.icao, e)
                pos = None

        # 2. Local decode using last known position for this aircraft
        if pos is None and ac.lat is not None:
            try:
                pos = pms.adsb.position_with_ref(msg, ac.lat, ac.lon)
            except Exception:
                pos = None

        # 3. Local decode using configured receiver reference
        if pos is None and self.lat_ref is not None:
            try:
                pos = pms.adsb.position_with_ref(
                    msg, self.lat_ref, self.lon_ref)
            except Exception:
                pos = None

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
                log.debug("rejecting teleport %s: %.3f,%.3f -> %.3f,%.3f",
                          ac.icao, ac.lat, ac.lon, new_lat, new_lon)
                return

        ac.lat = new_lat
        ac.lon = new_lon
        ac.last_position_time = now
        ac.trail.append((round(new_lat, 5), round(new_lon, 5), now))

    # -------- output --------

    def cleanup(self, now: float):
        stale = [icao for icao, ac in self.aircraft.items()
                 if now - ac.last_seen > AIRCRAFT_TIMEOUT]
        for icao in stale:
            del self.aircraft[icao]

    def snapshot(self, now: Optional[float] = None) -> dict:
        if now is None:
            now = time.time()
        out = []
        for ac in self.aircraft.values():
            # Skip aircraft we know nothing useful about yet
            if (ac.lat is None and ac.callsign is None
                    and ac.altitude is None and ac.squawk is None):
                continue
            out.append({
                "icao": ac.icao,
                "callsign": ac.callsign,
                "lat": ac.lat,
                "lon": ac.lon,
                "altitude": ac.altitude,
                "track": ac.track,
                "speed": ac.speed,
                "vrate": ac.vrate,
                "squawk": ac.squawk,
                "on_ground": ac.on_ground,
                "last_seen": ac.last_seen,
                "age": round(now - ac.last_seen, 1),
                "msg_count": ac.msg_count,
                "trail": [[lat, lon] for lat, lon, _ in ac.trail],
            })
        # Newest first
        out.sort(key=lambda a: a["last_seen"], reverse=True)
        return {
            "now": now,
            "count": len(out),
            "positioned": sum(1 for a in out if a["lat"] is not None),
            "lat_ref": self.lat_ref,
            "lon_ref": self.lon_ref,
            "aircraft": out,
        }
