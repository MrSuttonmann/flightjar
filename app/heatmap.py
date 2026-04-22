"""Per-hour / per-weekday traffic heatmap.

Counts the number of distinct aircraft-tracking events (one per call to
`observe(ts)`) that fell inside each of the 168 (day x hour) buckets.
Callers wire this up to fire once when the registry creates a fresh
Aircraft record — so re-sightings of the same tail across many days
each count once, and the result reads as "how many new planes did we
pick up in this hour slot".

Each (weekday, hour) slot resets when it comes around again — i.e. the
grid reads "last Monday's 09:00 count", not "summed across every Monday
09:00 we've ever seen". Without that, heavy slots would keep drifting
up relative to quiet ones and the opacity-normalised visualisation
would collapse toward a flat plate.

State is a flat 7x24 grid (plus a parallel 7x24 "last day number" grid
used to detect the weekly rollover) persisted to /data/heatmap.json.
Load on startup keeps the most recent week's picture across restarts.
"""

from __future__ import annotations

import json
import logging
import time
from datetime import UTC, datetime
from pathlib import Path

log = logging.getLogger("beast.heatmap")

DAYS = 7  # datetime.weekday(): 0=Mon … 6=Sun
HOURS = 24
DAY_LABELS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]


class TrafficHeatmap:
    def __init__(self, cache_path: Path | None = None) -> None:
        self.cache_path = cache_path
        self.grid: list[list[int]] = [[0] * HOURS for _ in range(DAYS)]
        # Day-number (unix seconds // 86400) of the most recent observation
        # in each cell. Used only to detect when a cell comes back around
        # to a new week and needs zeroing before the new count accumulates.
        # 0 means "never observed" — any real observation is > 0, so the
        # first-ever hit in a cell doesn't trigger a spurious reset.
        self.last_day: list[list[int]] = [[0] * HOURS for _ in range(DAYS)]
        self._dirty = False
        self._last_persist = 0.0
        self._load()

    def observe(self, ts: float) -> None:
        """Bump the (weekday, hour-of-day) bucket for this unix timestamp.

        If the cell hasn't been touched today, reset it first — this keeps
        each slot showing only the most recent week's worth of activity in
        that slot, not the all-time sum.
        """
        dt = datetime.fromtimestamp(ts, UTC)
        weekday = dt.weekday()
        hour = dt.hour
        if not (0 <= weekday < DAYS and 0 <= hour < HOURS):
            return
        day = int(ts // 86400)
        # Same day → just accumulate. Different day → new cycle for this
        # slot (can only be the same weekday+hour a week apart, so any day
        # delta means we've rolled over), wipe the old count first.
        if self.last_day[weekday][hour] and self.last_day[weekday][hour] != day:
            self.grid[weekday][hour] = 0
        self.grid[weekday][hour] += 1
        self.last_day[weekday][hour] = day
        self._dirty = True

    def snapshot(self) -> dict:
        hours = [sum(self.grid[d][h] for d in range(DAYS)) for h in range(HOURS)]
        days = [sum(row) for row in self.grid]
        return {
            "grid": self.grid,
            "day_labels": DAY_LABELS,
            "hours": hours,
            "days": days,
            "total": sum(days),
        }

    def reset(self) -> None:
        self.grid = [[0] * HOURS for _ in range(DAYS)]
        self.last_day = [[0] * HOURS for _ in range(DAYS)]
        self._dirty = True
        self._persist(force=True)

    # -------- persistence --------

    def _load(self) -> None:
        if self.cache_path is None or not self.cache_path.exists():
            return
        try:
            data = json.loads(self.cache_path.read_text(encoding="utf-8"))
        except Exception as e:
            log.warning("heatmap cache unreadable at %s: %s", self.cache_path, e)
            return
        grid = data.get("grid")
        if isinstance(grid, list) and len(grid) == DAYS:
            loaded = []
            for row in grid:
                if not isinstance(row, list):
                    return
                loaded.append([int(c or 0) for c in row[:HOURS]] + [0] * max(0, HOURS - len(row)))
            self.grid = loaded
            log.info("loaded traffic heatmap (%d events)", sum(sum(r) for r in self.grid))
        # last_day may be absent on legacy caches — default to "touched
        # today" for every populated cell so the current picture survives
        # the next observation without a surprise wipe. Each cell will
        # still reset at its natural weekly rollover going forward.
        last_day = data.get("last_day")
        if isinstance(last_day, list) and len(last_day) == DAYS:
            loaded_ld: list[list[int]] = []
            for row in last_day:
                if not isinstance(row, list):
                    loaded_ld = []
                    break
                loaded_ld.append(
                    [int(c or 0) for c in row[:HOURS]] + [0] * max(0, HOURS - len(row))
                )
            if loaded_ld:
                self.last_day = loaded_ld
        else:
            today = int(time.time() // 86400)
            self.last_day = [
                [today if self.grid[d][h] > 0 else 0 for h in range(HOURS)] for d in range(DAYS)
            ]

    def _persist(self, force: bool = False) -> None:
        if self.cache_path is None:
            return
        if not force and not self._dirty:
            return
        try:
            self.cache_path.parent.mkdir(parents=True, exist_ok=True)
            tmp = self.cache_path.with_suffix(self.cache_path.suffix + ".tmp")
            tmp.write_text(
                json.dumps(
                    {"grid": self.grid, "last_day": self.last_day},
                    separators=(",", ":"),
                ),
                encoding="utf-8",
            )
            tmp.replace(self.cache_path)
            self._dirty = False
            self._last_persist = time.time()
        except Exception as e:
            log.warning("couldn't persist heatmap: %s", e)

    def maybe_persist(self, interval: float = 60.0) -> None:
        if not self._dirty:
            return
        if time.time() - self._last_persist >= interval:
            self._persist()
