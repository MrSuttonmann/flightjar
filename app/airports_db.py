"""ICAO airport code → human-readable name / location.

Reads the OurAirports CSV (public domain). Data at
https://github.com/davidmegginson/ourairports-data — we parse at startup
and keep a dict in memory. Looked up by ICAO ident, which is what adsbdb
returns in `origin.icao_code` / `destination.icao_code`.

The file is baked into the Docker image at build time. If a user-provided
copy exists at /data/airports.csv it wins (same override pattern as the
aircraft DB), otherwise we fall back to the baked-in version.
"""

import csv
import logging
from pathlib import Path
from typing import Any

log = logging.getLogger("beast.airports_db")

DEFAULT_PATHS: list[Path] = [
    Path("/data/airports.csv"),
    Path(__file__).parent / "airports.csv",
]

# Types worth keeping. Heliports, closed, and seaplane bases clutter the
# database without adding much for ADS-B route display.
KEEP_TYPES = frozenset({"small_airport", "medium_airport", "large_airport"})
# Sort priority for bbox-culled responses — bigger airports win when the
# result hits the row limit so wide views still show all the majors.
TYPE_RANK = {"large_airport": 0, "medium_airport": 1, "small_airport": 2}


class AirportsDB:
    """In-memory ICAO → {name, city, country} lookup."""

    def __init__(self) -> None:
        self._db: dict[str, dict[str, Any]] = {}

    def __len__(self) -> int:
        return len(self._db)

    def __contains__(self, icao: str) -> bool:
        return icao.upper() in self._db

    def lookup(self, icao: str | None) -> dict[str, Any] | None:
        if not icao:
            return None
        return self._db.get(icao.upper())

    def load_from(self, path: Path) -> int:
        """Parse OurAirports CSV into a fresh dict and swap in atomically."""
        new_db: dict[str, dict[str, Any]] = {}
        with path.open("rt", encoding="utf-8", newline="") as fh:
            reader = csv.DictReader(fh)
            for row in reader:
                if row.get("type") not in KEEP_TYPES:
                    continue
                ident = (row.get("ident") or "").strip().upper()
                name = (row.get("name") or "").strip()
                if not ident or not name:
                    continue
                try:
                    lat = float(row.get("latitude_deg") or "")
                    lon = float(row.get("longitude_deg") or "")
                except ValueError:
                    # Entries without coordinates are useless to us (no map
                    # rendering, no bbox inclusion), so skip them entirely.
                    continue
                new_db[ident] = {
                    "name": name,
                    "city": (row.get("municipality") or "").strip(),
                    "country": (row.get("iso_country") or "").strip(),
                    "type": row["type"],
                    "lat": lat,
                    "lon": lon,
                }
        self._db = new_db
        return len(self._db)

    def bbox(
        self,
        min_lat: float,
        min_lon: float,
        max_lat: float,
        max_lon: float,
        limit: int = 2000,
    ) -> list[dict]:
        """Return airports inside the bounding box, biggest first.

        Handles antimeridian wrap when `min_lon > max_lon`. Truncates to
        `limit` rows sorted by type (large → medium → small), so a very
        wide view still includes all major airports.
        """
        wraps = min_lon > max_lon
        hits: list[dict] = []
        for icao, info in self._db.items():
            lat = info.get("lat")
            lon = info.get("lon")
            if lat is None or lon is None:
                continue
            if not (min_lat <= lat <= max_lat):
                continue
            in_lon = (lon >= min_lon or lon <= max_lon) if wraps else (min_lon <= lon <= max_lon)
            if not in_lon:
                continue
            hits.append(
                {
                    "icao": icao,
                    "name": info["name"],
                    "lat": lat,
                    "lon": lon,
                    "type": info.get("type", ""),
                }
            )
        hits.sort(key=lambda e: TYPE_RANK.get(e["type"], 9))
        return hits[:limit]

    def load_first_available(self, paths: list[Path] | None = None) -> int:
        for p in paths or DEFAULT_PATHS:
            if p.exists():
                try:
                    n = self.load_from(p)
                    log.info("loaded airports DB from %s (%d entries)", p, n)
                    return n
                except Exception as e:
                    log.warning("failed to load airports DB from %s: %s", p, e)
        log.info("no airports DB found; tooltips disabled")
        return 0
