"""Navaid identifier → coordinates + type.

Reads the OurAirports navaids CSV (public domain). Data at
https://github.com/davidmegginson/ourairports-data — parsed once at startup
and kept in memory. Drives the Navaids map overlay via the /api/navaids
bbox endpoint.

The file is baked into the Docker image at build time. If a user-provided
copy exists at /data/navaids.csv it wins (same override pattern as
airports.csv), otherwise we fall back to the baked-in version.
"""

import csv
import logging
from pathlib import Path
from typing import Any

log = logging.getLogger("beast.navaids_db")

DEFAULT_PATHS: list[Path] = [
    Path("/data/navaids.csv"),
    Path(__file__).parent / "navaids.csv",
]

# Types worth surfacing on the map. ILS glideslope / localizer / marker
# beacons aren't useful as independent overlay dots — they're always
# colocated with a runway threshold and would just clutter.
KEEP_TYPES = frozenset({"VOR", "VOR-DME", "VORTAC", "NDB", "NDB-DME", "DME", "TACAN"})

# Sort priority for bbox-culled responses — omnidirectional nav aids
# (VOR family) rank highest, DME-only and TACAN next, NDBs last. A wide
# view truncated to `limit` rows still includes the backbone of the
# airway network.
TYPE_RANK = {
    "VORTAC": 0,
    "VOR-DME": 0,
    "VOR": 1,
    "DME": 2,
    "TACAN": 2,
    "NDB-DME": 3,
    "NDB": 3,
}


class NavaidsDB:
    """In-memory ident → {name, type, lat, lon, frequency_khz} lookup."""

    def __init__(self) -> None:
        self._rows: list[dict[str, Any]] = []

    def __len__(self) -> int:
        return len(self._rows)

    def load_from(self, path: Path) -> int:
        """Parse OurAirports navaids CSV into a fresh list and swap in atomically."""
        new_rows: list[dict[str, Any]] = []
        with path.open("rt", encoding="utf-8", newline="") as fh:
            reader = csv.DictReader(fh)
            for row in reader:
                nav_type = (row.get("type") or "").strip().upper()
                if nav_type not in KEEP_TYPES:
                    continue
                ident = (row.get("ident") or "").strip().upper()
                name = (row.get("name") or "").strip()
                if not ident:
                    continue
                try:
                    lat = float(row.get("latitude_deg") or "")
                    lon = float(row.get("longitude_deg") or "")
                except ValueError:
                    continue
                freq_raw = (row.get("frequency_khz") or "").strip()
                try:
                    freq_khz: float | None = float(freq_raw) if freq_raw else None
                except ValueError:
                    freq_khz = None
                new_rows.append(
                    {
                        "ident": ident,
                        "name": name,
                        "type": nav_type,
                        "lat": lat,
                        "lon": lon,
                        "frequency_khz": freq_khz,
                        "country": (row.get("iso_country") or "").strip(),
                        "associated_airport": (row.get("associated_airport") or "").strip(),
                    }
                )
        self._rows = new_rows
        return len(self._rows)

    def bbox(
        self,
        min_lat: float,
        min_lon: float,
        max_lat: float,
        max_lon: float,
        limit: int = 2000,
    ) -> list[dict]:
        """Return navaids inside the bounding box, strongest-rank first.

        Handles antimeridian wrap when `min_lon > max_lon`. Truncates to
        `limit` rows sorted by type (VOR family → DME/TACAN → NDB).
        """
        wraps = min_lon > max_lon
        hits: list[dict] = []
        for r in self._rows:
            lat = r["lat"]
            lon = r["lon"]
            if not (min_lat <= lat <= max_lat):
                continue
            in_lon = (lon >= min_lon or lon <= max_lon) if wraps else (min_lon <= lon <= max_lon)
            if not in_lon:
                continue
            hits.append(r)
        hits.sort(key=lambda e: TYPE_RANK.get(e["type"], 9))
        return hits[:limit]

    def load_first_available(self, paths: list[Path] | None = None) -> int:
        for p in paths or DEFAULT_PATHS:
            if p.exists():
                try:
                    n = self.load_from(p)
                    log.info("loaded navaids DB from %s (%d entries)", p, n)
                    return n
                except Exception as e:
                    log.warning("failed to load navaids DB from %s: %s", p, e)
        log.info("no navaids DB found; overlay disabled")
        return 0
