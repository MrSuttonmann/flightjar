"""Persistent watchlist of ICAO24 hex codes.

Mirrored from the browser's localStorage via the /api/watchlist endpoints.
Server-side notifications (Telegram, ntfy, generic webhook) fire on
snapshot ticks when a watched aircraft appears, so alerts work even
when no browser tab is open.

Single-writer model: all mutations run on the asyncio event loop, so
we don't need a lock. Persistence is atomic via write-to-temp + rename.
"""

from __future__ import annotations

import json
import logging
import os
import re
from pathlib import Path

log = logging.getLogger("beast.watchlist")

_HEX_RE = re.compile(r"^[0-9a-f]{6}$")
_CACHE_SCHEMA_VERSION = 1


def _normalise(icao: object) -> str | None:
    """Lowercase + validate an ICAO24 hex. Returns None for anything
    that isn't a 6-char lowercase hex string."""
    if not isinstance(icao, str):
        return None
    k = icao.strip().lower()
    return k if _HEX_RE.match(k) else None


class WatchlistStore:
    """In-memory set of ICAO24 codes with atomic on-disk persistence."""

    def __init__(self, path: Path | None = None) -> None:
        self.path = path
        self._set: set[str] = set()
        self._load()

    # -------- public API --------

    def __len__(self) -> int:
        return len(self._set)

    def __contains__(self, icao: object) -> bool:
        k = _normalise(icao)
        return k is not None and k in self._set

    def has(self, icao: str) -> bool:
        return icao in self

    def get(self) -> list[str]:
        """Return the watchlist as a sorted list of normalised hex codes."""
        return sorted(self._set)

    def replace(self, icao24s: list[object] | set[object]) -> list[str]:
        """Atomically swap the watchlist contents. Invalid entries are
        dropped silently. Persists to disk when the set actually
        changed. Returns the new sorted list."""
        new = {k for k in (_normalise(x) for x in icao24s) if k}
        if new == self._set:
            return sorted(self._set)
        self._set = new
        self._persist()
        return sorted(self._set)

    # -------- persistence --------

    def _load(self) -> None:
        if self.path is None or not self.path.exists():
            return
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
        except Exception as e:
            log.warning("watchlist store unreadable at %s: %s", self.path, e)
            return
        if not isinstance(data, dict):
            return
        entries = data.get("icao24s")
        if not isinstance(entries, list):
            return
        self._set = {k for k in (_normalise(x) for x in entries) if k}
        log.info("loaded %d watchlist entries from %s", len(self._set), self.path)

    def _persist(self) -> None:
        if self.path is None:
            return
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            tmp = self.path.with_suffix(self.path.suffix + ".tmp")
            tmp.write_text(
                json.dumps(
                    {"version": _CACHE_SCHEMA_VERSION, "icao24s": sorted(self._set)},
                    separators=(",", ":"),
                ),
                encoding="utf-8",
            )
            os.replace(tmp, self.path)
        except Exception as e:
            log.warning("couldn't persist watchlist to %s: %s", self.path, e)
