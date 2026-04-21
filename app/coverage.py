"""Per-bearing maximum-range tracker for the receiver's polar coverage.

For every decoded aircraft position we compute the bearing + distance
from the receiver and keep the maximum range observed in each bearing
bucket. The result is a polar "coverage map" that reflects real-world
reception (terrain, antenna pattern, obstructions) rather than an
abstract circular range ring.

Kept deliberately small and allocation-free on the hot path: a fixed
list of floats, one slot per bearing bucket, updated in place. Doesn't
decay over time — max is max — but the user can reset via an endpoint
if they move antennas.
"""

import logging
import math
import time
from collections.abc import Callable
from pathlib import Path

log = logging.getLogger("beast.coverage")

# 36 buckets = 10° each. Coarser than e.g. 1° but more than enough to
# render a smooth polygon at typical zoom levels, and keeps memory +
# persisted state tiny (a 36-float list).
BUCKETS = 36
BUCKET_DEG = 360.0 / BUCKETS
EARTH_KM = 6371.0


def _bearing(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    """Initial true bearing (degrees, 0=N, 90=E) from point 1 to point 2."""
    phi1 = math.radians(lat1)
    phi2 = math.radians(lat2)
    dlon = math.radians(lon2 - lon1)
    y = math.sin(dlon) * math.cos(phi2)
    x = math.cos(phi1) * math.sin(phi2) - math.sin(phi1) * math.cos(phi2) * math.cos(dlon)
    return (math.degrees(math.atan2(y, x)) + 360.0) % 360.0


def _haversine_km(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    phi1 = math.radians(lat1)
    phi2 = math.radians(lat2)
    dphi = math.radians(lat2 - lat1)
    dlam = math.radians(lon2 - lon1)
    a = math.sin(dphi / 2) ** 2 + math.cos(phi1) * math.cos(phi2) * math.sin(dlam / 2) ** 2
    return 2 * EARTH_KM * math.asin(min(1.0, math.sqrt(a)))


class PolarCoverage:
    """Per-bucket maximum-distance tracker, with lazy persistence."""

    def __init__(
        self,
        receiver_lat: float | None = None,
        receiver_lon: float | None = None,
        cache_path: Path | None = None,
    ) -> None:
        self.receiver_lat = receiver_lat
        self.receiver_lon = receiver_lon
        self.cache_path = cache_path
        self.maxdist: list[float] = [0.0] * BUCKETS
        self._dirty = False
        self._last_persist = 0.0
        # Optional (bearing_deg, dist_km) callback fired when a bucket
        # gets a new max. main.py wires this up to push a "new range
        # record" event into the snapshot so the frontend can toast it.
        self.on_new_max: Callable[[float, float], None] | None = None
        self._load()

    def set_receiver(self, lat: float | None, lon: float | None) -> None:
        self.receiver_lat = lat
        self.receiver_lon = lon

    def observe(self, lat: float, lon: float) -> None:
        """Record an aircraft position. No-op if the receiver isn't located."""
        if self.receiver_lat is None or self.receiver_lon is None:
            return
        dist = _haversine_km(self.receiver_lat, self.receiver_lon, lat, lon)
        if dist <= 0:
            return
        bearing = _bearing(self.receiver_lat, self.receiver_lon, lat, lon)
        bucket = int(bearing // BUCKET_DEG) % BUCKETS
        if dist > self.maxdist[bucket]:
            self.maxdist[bucket] = dist
            self._dirty = True
            if self.on_new_max is not None:
                try:
                    # Emit the centre bearing of the bucket (not the
                    # exact bearing of the fix) so repeated records in
                    # the same bucket speak with one voice.
                    angle = bucket * BUCKET_DEG + BUCKET_DEG / 2
                    self.on_new_max(angle, dist)
                except Exception as e:
                    log.debug("coverage on_new_max callback failed: %s", e)

    def reset(self) -> None:
        self.maxdist = [0.0] * BUCKETS
        self._dirty = True
        self._persist(force=True)

    def snapshot(self) -> dict:
        """Serialisable view for the API consumer."""
        return {
            "receiver": {"lat": self.receiver_lat, "lon": self.receiver_lon},
            "bucket_deg": BUCKET_DEG,
            "bearings": [
                {"angle": i * BUCKET_DEG + BUCKET_DEG / 2, "dist_km": round(d, 2)}
                for i, d in enumerate(self.maxdist)
                if d > 0
            ],
        }

    # -------- persistence --------

    def _load(self) -> None:
        if self.cache_path is None or not self.cache_path.exists():
            return
        try:
            import json

            with self.cache_path.open("r", encoding="utf-8") as fh:
                data = json.load(fh)
        except Exception as e:
            log.warning("coverage cache unreadable at %s: %s", self.cache_path, e)
            return
        entries = data.get("maxdist") or []
        if isinstance(entries, list) and len(entries) == BUCKETS:
            self.maxdist = [float(x or 0.0) for x in entries]
            log.info("loaded polar coverage cache (%d buckets)", BUCKETS)

    def _persist(self, force: bool = False) -> None:
        if self.cache_path is None:
            return
        if not force and not self._dirty:
            return
        try:
            import json

            self.cache_path.parent.mkdir(parents=True, exist_ok=True)
            tmp = self.cache_path.with_suffix(self.cache_path.suffix + ".tmp")
            with tmp.open("w", encoding="utf-8") as fh:
                json.dump({"maxdist": self.maxdist}, fh, separators=(",", ":"))
            tmp.replace(self.cache_path)
            self._dirty = False
            self._last_persist = time.time()
        except Exception as e:
            log.warning("couldn't persist polar coverage: %s", e)

    def maybe_persist(self, interval: float = 60.0) -> None:
        """Call periodically; writes to disk no more often than `interval` s."""
        if not self._dirty:
            return
        if time.time() - self._last_persist >= interval:
            self._persist()
