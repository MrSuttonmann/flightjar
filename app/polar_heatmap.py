"""Per-(bearing x distance) observation-density tracker, 7-day window.

Counts how many position fixes landed in each cell of a polar grid
anchored on the receiver: 36 bearing buckets (10° each) x BANDS
distance bands (BAND_KM wide, capped at BANDS*BAND_KM). Every fix
beyond the cap falls into the outermost band rather than being
dropped, so a distant aircraft still registers.

Counts are kept on a 7-day rolling window: each observation is stamped
into a per-day sub-grid, and any sub-grid older than WINDOW_DAYS is
pruned. Without the window, heavy-traffic cells accumulate without
bound and the opacity-normalised visualisation collapses — every
sector ends up at max and the density contrast disappears.

The result is a receiver "reception pattern" — bright cells show
where you get lots of signal (good line of sight, open terrain),
dim cells show where terrain or antenna geometry attenuates you.
Distinct from `PolarCoverage` (which only tracks max range per
bearing): this is density, not reach.
"""

from __future__ import annotations

import json
import logging
import time
from collections.abc import Callable
from pathlib import Path

from .coverage import BUCKET_DEG, BUCKETS, _bearing, _haversine_km

log = logging.getLogger("beast.polar_heatmap")

BAND_KM = 25.0
BANDS = 12  # 12 * 25 km = 300 km ceiling (anything beyond lands in the last band)
WINDOW_DAYS = 7


def _empty_grid() -> list[list[int]]:
    return [[0] * BANDS for _ in range(BUCKETS)]


class PolarHeatmap:
    """BUCKETS x BANDS count grid over a rolling WINDOW_DAYS window."""

    def __init__(
        self,
        receiver_lat: float | None = None,
        receiver_lon: float | None = None,
        cache_path: Path | None = None,
        time_fn: Callable[[], float] | None = None,
    ) -> None:
        self.receiver_lat = receiver_lat
        self.receiver_lon = receiver_lon
        self.cache_path = cache_path
        self._time_fn = time_fn or time.time
        # Keyed by day-number (unix seconds // 86400). A dict instead of a
        # fixed-length ring so the implementation doesn't need to track
        # "the current day index" — prune by age on every observe.
        self._daily_grids: dict[int, list[list[int]]] = {}
        self._dirty = False
        self._last_persist = 0.0
        self._load()

    def set_receiver(self, lat: float | None, lon: float | None) -> None:
        self.receiver_lat = lat
        self.receiver_lon = lon

    def observe(self, lat: float, lon: float, now: float | None = None) -> None:
        """Bump the (bearing, distance) cell for an aircraft position."""
        if self.receiver_lat is None or self.receiver_lon is None:
            return
        dist = _haversine_km(self.receiver_lat, self.receiver_lon, lat, lon)
        if dist <= 0:
            return
        bearing = _bearing(self.receiver_lat, self.receiver_lon, lat, lon)
        bucket = int(bearing // BUCKET_DEG) % BUCKETS
        band = min(BANDS - 1, int(dist // BAND_KM))
        day = int((now if now is not None else self._time_fn()) // 86400)
        self._prune(day)
        grid = self._daily_grids.get(day)
        if grid is None:
            grid = _empty_grid()
            self._daily_grids[day] = grid
        grid[bucket][band] += 1
        self._dirty = True

    def _prune(self, today: int) -> None:
        cutoff = today - (WINDOW_DAYS - 1)
        stale = [d for d in self._daily_grids if d < cutoff]
        for d in stale:
            del self._daily_grids[d]
        if stale:
            self._dirty = True

    def reset(self) -> None:
        self._daily_grids = {}
        self._dirty = True
        self._persist(force=True)

    def _combined_grid(self) -> list[list[int]]:
        """Sum every in-window day grid into a single BUCKETS x BANDS grid."""
        out = _empty_grid()
        for day_grid in self._daily_grids.values():
            for b in range(BUCKETS):
                row_out = out[b]
                row_in = day_grid[b]
                for d in range(BANDS):
                    row_out[d] += row_in[d]
        return out

    def snapshot(self, now: float | None = None) -> dict:
        """Serialisable view for the API consumer.

        Prunes first so a snapshot taken after a quiet stretch reflects the
        rolling window even if no observations have arrived to trigger a
        prune recently.
        """
        today = int((now if now is not None else self._time_fn()) // 86400)
        self._prune(today)
        grid = self._combined_grid()
        total = sum(sum(row) for row in grid)
        return {
            "receiver": {"lat": self.receiver_lat, "lon": self.receiver_lon},
            "bucket_deg": BUCKET_DEG,
            "band_km": BAND_KM,
            "bands": BANDS,
            "window_days": WINDOW_DAYS,
            "grid": grid,
            "total": total,
        }

    # -------- persistence --------

    def _load(self) -> None:
        if self.cache_path is None or not self.cache_path.exists():
            return
        try:
            data = json.loads(self.cache_path.read_text(encoding="utf-8"))
        except Exception as e:
            log.warning("polar heatmap cache unreadable at %s: %s", self.cache_path, e)
            return
        daily = data.get("daily_grids")
        if isinstance(daily, dict):
            loaded: dict[int, list[list[int]]] = {}
            for key, val in daily.items():
                try:
                    day = int(key)
                except (TypeError, ValueError):
                    continue
                grid = self._coerce_grid(val)
                if grid is not None:
                    loaded[day] = grid
            today = int(self._time_fn() // 86400)
            cutoff = today - (WINDOW_DAYS - 1)
            self._daily_grids = {d: g for d, g in loaded.items() if d >= cutoff}
            total = sum(sum(sum(row) for row in g) for g in self._daily_grids.values())
            log.info(
                "loaded polar heatmap (%d fixes over %d days in window)",
                total,
                len(self._daily_grids),
            )
            return
        # Legacy format: single cumulative grid. Best-effort migration —
        # stamp it into today's slot so it stays visible for one window
        # rather than evaporating, but won't outlive the rolling window.
        legacy = self._coerce_grid(data.get("grid"))
        if legacy is not None:
            today = int(self._time_fn() // 86400)
            self._daily_grids = {today: legacy}
            log.info(
                "migrated legacy polar heatmap into rolling window (%d fixes)",
                sum(sum(r) for r in legacy),
            )

    @staticmethod
    def _coerce_grid(val: object) -> list[list[int]] | None:
        """Validate/pad a grid loaded from disk; None on malformed input."""
        if not isinstance(val, list) or len(val) != BUCKETS:
            return None
        out: list[list[int]] = []
        for row in val:
            if not isinstance(row, list):
                return None
            ints = [int(c or 0) for c in row[:BANDS]]
            ints += [0] * max(0, BANDS - len(ints))
            out.append(ints)
        return out

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
                    {"daily_grids": {str(d): g for d, g in self._daily_grids.items()}},
                    separators=(",", ":"),
                ),
                encoding="utf-8",
            )
            tmp.replace(self.cache_path)
            self._dirty = False
            self._last_persist = self._time_fn()
        except Exception as e:
            log.warning("couldn't persist polar heatmap: %s", e)

    def maybe_persist(self, interval: float = 60.0) -> None:
        if not self._dirty:
            return
        if self._time_fn() - self._last_persist >= interval:
            self._persist()
