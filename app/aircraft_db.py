"""Aircraft registry / type lookup (ICAO24 → registration + type).

Reads a gzipped semicolon-separated CSV in the tar1090-db / Mictronics shape:

    icao24_hex;registration;type_icao;flags;type_long;;;

Looked up by the hex ICAO24 address we already track. Loading is opt-in —
if no DB file is present the registry stays empty and enrichment silently
no-ops.
"""

import contextlib
import csv
import gzip
import logging
import os
import urllib.request
from pathlib import Path

log = logging.getLogger("beast.aircraft_db")

# Checked in order; first existing file wins. The /data path lets users drop
# a fresher DB in via the volume mount; the in-image path is the fallback
# baked at build time (if the Dockerfile downloads one).
DEFAULT_PATHS: list[Path] = [
    Path("/data/aircraft_db.csv.gz"),
    Path(__file__).parent / "aircraft_db.csv.gz",
]

DEFAULT_REFRESH_URL = "https://github.com/wiedehopf/tar1090-db/raw/refs/heads/csv/aircraft.csv.gz"


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
        """Load entries from a gzipped CSV; swap in atomically on success."""
        new_db: dict[str, dict[str, str | None]] = {}
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
                    new_db[icao] = {
                        "registration": reg,
                        "type_icao": type_icao,
                        "type_long": type_long,
                    }
        # Swap only after full parse — partial reads never replace live data.
        self._db = new_db
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

    def refresh_from_url(
        self,
        url: str,
        target_path: Path,
        timeout: float = 60.0,
    ) -> int:
        """Download a fresh DB, validate by parsing, persist, and swap in-memory.

        On any failure the existing in-memory DB is untouched. The downloaded
        file is written to a temp path first and only renamed into place after
        a successful parse, so a corrupted download doesn't poison the on-disk
        override either.
        """
        target_path.parent.mkdir(parents=True, exist_ok=True)
        tmp = target_path.with_suffix(target_path.suffix + ".tmp")
        try:
            with urllib.request.urlopen(url, timeout=timeout) as resp:
                tmp.write_bytes(resp.read())
            n = self.load_from(tmp)  # raises if the download is unreadable
            os.replace(tmp, target_path)
            log.info("refreshed aircraft DB from %s (%d entries)", url, n)
            return n
        finally:
            with contextlib.suppress(FileNotFoundError):
                tmp.unlink()
