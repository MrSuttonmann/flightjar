"""Airline registry (ICAO airline code → IATA + name + alliance).

Reads OpenFlights' `airlines.dat` (ODbL-licensed, attribution required) and
indexes the `Active == 'Y'` rows by their 3-letter ICAO airline code. That's
the same prefix that shows up on the front of ADS-B callsigns, so snapshot
enrichment can map a callsign like `BAW123` straight to the operator record
without relying on adsbdb's full-name string.

Alliance membership doesn't ship with OpenFlights data, so we carry a tiny
hand-curated dict below for the three large alliances. Misses don't break
anything — an airline that isn't in the alliance dict simply has no
alliance tag.
"""

from __future__ import annotations

import csv
import logging
from pathlib import Path

log = logging.getLogger("beast.airlines_db")

DEFAULT_PATHS: list[Path] = [
    Path("/data/airlines.dat"),
    Path(__file__).parent / "airlines.dat",
]

# OpenFlights uses `\N` as its NULL marker. IATA entries of "-" and ICAO
# entries of "N/A" / "" also mean "no code"; skip them.
_NULL_MARKERS = frozenset(["", r"\N", "-", "N/A"])

# Alliance membership keyed by ICAO airline code. Kept intentionally narrow
# to well-established carriers — mis-labelling an airline is worse than
# omitting one. Check https://www.staralliance.com / oneworld.com /
# skyteam.com when adding entries.
ALLIANCES: dict[str, str] = {
    # Star Alliance
    "UAL": "star",
    "DLH": "star",
    "SIA": "star",
    "ANA": "star",
    "THY": "star",
    "ANZ": "star",
    "SAS": "star",
    "TAP": "star",
    "ETH": "star",
    "ACA": "star",
    "SWR": "star",
    "LOT": "star",
    "EVA": "star",
    "AUA": "star",
    "AVA": "star",
    "AAR": "star",
    "CCA": "star",
    "COP": "star",
    "EGY": "star",
    "BEL": "star",
    "CSZ": "star",
    # oneworld
    "AAL": "oneworld",
    "BAW": "oneworld",
    "CPA": "oneworld",
    "FIN": "oneworld",
    "IBE": "oneworld",
    "JAL": "oneworld",
    "QFA": "oneworld",
    "QTR": "oneworld",
    "MAS": "oneworld",
    "RJA": "oneworld",
    "ALK": "oneworld",
    # SkyTeam
    "AFR": "skyteam",
    "DAL": "skyteam",
    "KLM": "skyteam",
    "AMX": "skyteam",
    "CES": "skyteam",
    "CSN": "skyteam",
    "KAL": "skyteam",
    "SVA": "skyteam",
    "ITY": "skyteam",
    "RAM": "skyteam",
    "KQA": "skyteam",
    "AEA": "skyteam",
    "GIA": "skyteam",
    "MEA": "skyteam",
    "CAL": "skyteam",
    "VIR": "skyteam",
}


def _clean(value: str | None) -> str | None:
    """Normalise an OpenFlights cell, collapsing NULL markers to None."""
    if value is None:
        return None
    v = value.strip()
    if v in _NULL_MARKERS:
        return None
    return v


class AirlinesDB:
    """In-memory ICAO airline-code → airline-record lookup."""

    def __init__(self) -> None:
        self._db: dict[str, dict[str, str | None]] = {}

    def __len__(self) -> int:
        return len(self._db)

    def __contains__(self, icao: str) -> bool:
        return bool(icao) and icao.upper() in self._db

    def lookup_by_icao(self, icao: str | None) -> dict[str, str | None] | None:
        """Return the airline record for this ICAO code, or None."""
        if not icao:
            return None
        return self._db.get(icao.upper())

    def lookup_by_callsign(self, callsign: str | None) -> dict[str, str | None] | None:
        """Return the airline record whose ICAO code matches this callsign's
        3-letter prefix. Falls through to None for GA / military / numeric
        callsigns that aren't airline-operated."""
        if not callsign or len(callsign) < 3:
            return None
        prefix = callsign[:3].upper()
        # Only alphabetic prefixes are valid ICAO airline codes. This skips
        # ad-hoc numeric callsigns and military tags that start with digits.
        if not prefix.isalpha():
            return None
        return self._db.get(prefix)

    def load_from(self, path: Path) -> int:
        """Load entries from an OpenFlights-format `airlines.dat`; swap in
        atomically on success. Only keeps `Active == 'Y'` rows with a real
        3-letter ICAO code so defunct carriers and callsign-only entries
        don't leak through."""
        new_db: dict[str, dict[str, str | None]] = {}
        with path.open("rt", encoding="utf-8", newline="") as fh:
            reader = csv.reader(fh)
            for row in reader:
                if len(row) < 8:
                    continue
                name = _clean(row[1])
                iata = _clean(row[3])
                icao = _clean(row[4])
                callsign = _clean(row[5])
                country = _clean(row[6])
                active = _clean(row[7])
                if not icao or len(icao) != 3 or not icao.isalpha():
                    continue
                if active != "Y":
                    continue
                icao_upper = icao.upper()
                # A handful of ICAO codes appear twice in the dataset
                # (mergers, name-changes). Keep whichever row comes last —
                # OpenFlights tends to append rather than edit in place.
                new_db[icao_upper] = {
                    "name": name,
                    "iata": iata.upper() if iata else None,
                    "icao": icao_upper,
                    "callsign": callsign,
                    "country": country,
                    "alliance": ALLIANCES.get(icao_upper),
                }
        self._db = new_db
        return len(self._db)

    def load_first_available(self, paths: list[Path] | None = None) -> int:
        for p in paths or DEFAULT_PATHS:
            if p.exists():
                try:
                    n = self.load_from(p)
                    log.info("loaded airlines DB from %s (%d entries)", p, n)
                    return n
                except Exception as e:
                    log.warning("failed to load airlines DB from %s: %s", p, e)
        log.info("no airlines DB found; airline enrichment disabled")
        return 0
