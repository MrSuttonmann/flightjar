"""Aircraft registry / type lookup (ICAO24 → registration + type).

Reads a gzipped semicolon-separated CSV in the tar1090-db / Mictronics shape:

    icao24_hex;registration;type_icao;flags;type_long;;;

Looked up by the hex ICAO24 address we already track. Loading is opt-in —
if no DB file is present the registry stays empty and enrichment silently
no-ops.
"""

import csv
import gzip
import logging
from pathlib import Path

log = logging.getLogger("beast.aircraft_db")

# Checked in order; first existing file wins. The /data path lets users drop
# a fresher DB in via the volume mount; the in-image path is the fallback
# baked at build time (if the Dockerfile downloads one).
DEFAULT_PATHS: list[Path] = [
    Path("/data/aircraft_db.csv.gz"),
    Path(__file__).parent / "aircraft_db.csv.gz",
]


class AircraftDB:
    """In-memory ICAO24 → registration/type lookup."""

    def __init__(self) -> None:
        self._db: dict[str, dict[str, str | None]] = {}

    def __len__(self) -> int:
        return len(self._db)

    def __contains__(self, icao: str) -> bool:
        return icao.lower() in self._db

    def lookup(self, icao: str) -> dict[str, str | None] | None:
        return self._db.get(icao.lower())

    def load_from(self, path: Path) -> int:
        """Load entries from a gzipped semicolon-separated CSV. Returns row count."""
        with gzip.open(path, "rt", encoding="utf-8", errors="replace") as fh:
            reader = csv.reader(fh, delimiter=";")
            for row in reader:
                if len(row) < 5 or not row[0]:
                    continue
                icao = row[0].strip().lower()
                reg = (row[1] or "").strip() or None
                type_icao = (row[2] or "").strip() or None
                type_long = (row[4] or "").strip() or None
                if reg or type_icao or type_long:
                    self._db[icao] = {
                        "registration": reg,
                        "type_icao": type_icao,
                        "type_long": type_long,
                    }
        return len(self._db)

    def load_first_available(self, paths: list[Path] | None = None) -> int:
        """Try each candidate path in order; load the first one that exists."""
        for p in paths or DEFAULT_PATHS:
            if p.exists():
                try:
                    n = self.load_from(p)
                    log.info("loaded aircraft DB from %s (%d entries)", p, n)
                    return n
                except Exception as e:
                    log.warning("failed to load aircraft DB from %s: %s", p, e)
        log.info("no aircraft DB found; enrichment disabled")
        return 0
