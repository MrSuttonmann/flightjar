"""Persistent watchlist of ICAO24 hex codes.

Mirrored from the browser's localStorage via the /api/watchlist endpoints.
Server-side notifications (Telegram, ntfy, generic webhook) fire on
snapshot ticks when a watched aircraft appears, so alerts work even
when no browser tab is open.

Also tracks a `last_seen` unix timestamp per watched icao so the UI's
watchlist dialog can surface "last seen 3h ago" for tails that aren't
currently in coverage. The data is naturally server-side: the server
already sees every message, the watchlist lives here too, and the
timestamp then survives browser-storage resets + multi-device use.

Single-writer model: all mutations run on the asyncio event loop, so
we don't need a lock. Persistence is atomic via write-to-temp + rename.
"""

from __future__ import annotations

import json
import logging
import os
import re
import time
from pathlib import Path

log = logging.getLogger("beast.watchlist")

_HEX_RE = re.compile(r"^[0-9a-f]{6}$")
_CACHE_SCHEMA_VERSION = 2

# Writes to the last_seen map are debounced — a watchlisted plane in
# coverage would otherwise cause a disk rewrite every snapshot tick
# (once per second). 30 s means we lose at most that much data on an
# ungraceful shutdown, which is re-recorded the next time the plane
# passes.
_PERSIST_DEBOUNCE_S = 30.0


def _normalise(icao: object) -> str | None:
    """Lowercase + validate an ICAO24 hex. Returns None for anything
    that isn't a 6-char lowercase hex string."""
    if not isinstance(icao, str):
        return None
    k = icao.strip().lower()
    return k if _HEX_RE.match(k) else None


class WatchlistStore:
    """In-memory watchlist + last-seen map with atomic on-disk persistence."""

    def __init__(self, path: Path | None = None) -> None:
        self.path = path
        self._set: set[str] = set()
        self._last_seen: dict[str, float] = {}
        # Timestamp of the last flush to disk; used to throttle writes
        # from the hot `record_seen` path.
        self._last_persist_ts: float = 0.0
        self._load()

    # -------- public API --------

    def __len__(self) -> int:
        return len(self._set)

    def __contains__(self, icao: object) -> bool:
        k = _normalise(icao)
        return k is not None and k in self._set

    def has(self, icao: str) -> bool:
        return icao in self

    def get(self) -> dict[str, object]:
        """Return the full watchlist payload the HTTP endpoint serves:
        the sorted icao list plus the last-seen map."""
        return {
            "icao24s": sorted(self._set),
            "last_seen": dict(self._last_seen),
        }

    def icao24s(self) -> list[str]:
        """Sorted list of watchlisted icao24 hex codes."""
        return sorted(self._set)

    def replace(self, icao24s: list[object] | set[object]) -> dict[str, object]:
        """Atomically swap the watchlist contents. Invalid entries are
        dropped silently. Prunes last-seen entries for icaos that are
        no longer watchlisted. Persists when anything actually changed.
        Returns the new full payload."""
        new = {k for k in (_normalise(x) for x in icao24s) if k}
        pruned_last_seen = {k: v for k, v in self._last_seen.items() if k in new}
        if new == self._set and pruned_last_seen == self._last_seen:
            return self.get()
        self._set = new
        self._last_seen = pruned_last_seen
        self._persist()
        return self.get()

    def record_seen(self, icao: str, ts: float | None) -> None:
        """Note that a watchlisted icao was observed at `ts` (unix
        seconds). Silently ignores non-watchlisted icaos, empty ts,
        and time-travel (older ts than the current record). Disk
        flushes are debounced; first-ever sighting for an icao always
        flushes immediately so the new entry can't be lost to a crash."""
        key = _normalise(icao)
        if key is None or key not in self._set:
            return
        if not ts or ts <= 0:
            return
        previous = self._last_seen.get(key)
        if previous is not None and ts <= previous:
            return
        self._last_seen[key] = ts
        now = time.time()
        is_new = previous is None
        if is_new or (now - self._last_persist_ts) >= _PERSIST_DEBOUNCE_S:
            self._persist()

    def flush(self) -> None:
        """Force-persist any pending in-memory updates. Called on graceful
        shutdown so we don't lose up to `_PERSIST_DEBOUNCE_S` of data."""
        self._persist()

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
        # v2 adds the last-seen map; v1 files simply lack it, which
        # reloads gracefully with an empty dict.
        raw_last_seen = data.get("last_seen")
        if isinstance(raw_last_seen, dict):
            cleaned: dict[str, float] = {}
            for raw_key, raw_ts in raw_last_seen.items():
                key = _normalise(raw_key)
                if key is None or key not in self._set:
                    continue
                if not isinstance(raw_ts, int | float):
                    continue
                ts = float(raw_ts)
                if ts > 0:
                    cleaned[key] = ts
            self._last_seen = cleaned
        log.info(
            "loaded %d watchlist entries (%d with last-seen) from %s",
            len(self._set), len(self._last_seen), self.path,
        )

    def _persist(self) -> None:
        if self.path is None:
            return
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            tmp = self.path.with_suffix(self.path.suffix + ".tmp")
            tmp.write_text(
                json.dumps(
                    {
                        "version": _CACHE_SCHEMA_VERSION,
                        "icao24s": sorted(self._set),
                        "last_seen": self._last_seen,
                    },
                    separators=(",", ":"),
                ),
                encoding="utf-8",
            )
            os.replace(tmp, self.path)
            self._last_persist_ts = time.time()
        except Exception as e:
            log.warning("couldn't persist watchlist to %s: %s", self.path, e)
