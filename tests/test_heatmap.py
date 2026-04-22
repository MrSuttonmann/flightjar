"""Tests for the per-weekday / per-hour traffic heatmap."""

import json
from datetime import UTC, datetime
from pathlib import Path

from app.heatmap import DAY_LABELS, DAYS, HOURS, TrafficHeatmap


def _ts(year: int, month: int, day: int, hour: int) -> float:
    """UTC unix timestamp. Use dates whose weekday we know."""
    return datetime(year, month, day, hour, tzinfo=UTC).timestamp()


def test_observe_increments_weekday_hour_bucket(tmp_path: Path):
    h = TrafficHeatmap(cache_path=tmp_path / "heatmap.json")
    # 2024-01-01 was a Monday (weekday=0), hour 12.
    h.observe(_ts(2024, 1, 1, 12))
    h.observe(_ts(2024, 1, 1, 12))
    assert h.grid[0][12] == 2
    # All other buckets stay zero.
    assert sum(sum(row) for row in h.grid) == 2


def test_slot_resets_when_week_rolls_over(tmp_path: Path):
    """Each (weekday, hour) slot should only count its most recent
    occurrence's worth of activity — observations a week apart in the
    same slot must not accumulate."""
    h = TrafficHeatmap(cache_path=tmp_path / "heatmap.json")
    # Two observations on Mon 2024-01-01 at 12:xx land in the same
    # same-day hit → accumulate.
    h.observe(_ts(2024, 1, 1, 12))
    h.observe(_ts(2024, 1, 1, 12))
    assert h.grid[0][12] == 2
    # A week later (Mon 2024-01-08, same hour) — the slot rolls over
    # and the new week resets the cell before incrementing.
    h.observe(_ts(2024, 1, 8, 12))
    assert h.grid[0][12] == 1
    # Another hit within the same day → accumulates again.
    h.observe(_ts(2024, 1, 8, 12))
    assert h.grid[0][12] == 2


def test_other_slots_unaffected_by_rollover(tmp_path: Path):
    """Rolling over the Monday 12:00 slot mustn't touch any other cell."""
    h = TrafficHeatmap(cache_path=tmp_path / "heatmap.json")
    h.observe(_ts(2024, 1, 1, 12))  # Mon 12
    h.observe(_ts(2024, 1, 3, 14))  # Wed 14 — different slot
    h.observe(_ts(2024, 1, 8, 12))  # Mon 12 a week later → Mon 12 resets
    assert h.grid[0][12] == 1
    assert h.grid[2][14] == 1  # Wed 14 left alone


def test_snapshot_totals_match_grid_sum(tmp_path: Path):
    h = TrafficHeatmap(cache_path=tmp_path / "heatmap.json")
    h.observe(_ts(2024, 1, 1, 9))  # Mon 09
    h.observe(_ts(2024, 1, 3, 14))  # Wed 14
    snap = h.snapshot()
    assert snap["total"] == 2
    assert sum(snap["hours"]) == 2
    assert sum(snap["days"]) == 2
    assert snap["day_labels"] == DAY_LABELS
    assert len(snap["grid"]) == DAYS
    assert all(len(row) == HOURS for row in snap["grid"])


def test_reset_clears_all_buckets_and_persists(tmp_path: Path):
    path = tmp_path / "heatmap.json"
    h = TrafficHeatmap(cache_path=path)
    h.observe(_ts(2024, 1, 1, 12))
    assert h.snapshot()["total"] == 1
    h.reset()
    assert h.snapshot()["total"] == 0
    # Reset persisted too — a fresh instance on the same file starts zeroed.
    h2 = TrafficHeatmap(cache_path=path)
    assert h2.snapshot()["total"] == 0


def test_persistence_roundtrips(tmp_path: Path):
    path = tmp_path / "heatmap.json"
    h1 = TrafficHeatmap(cache_path=path)
    h1.observe(_ts(2024, 1, 1, 12))  # Mon 12
    h1.observe(_ts(2024, 1, 5, 23))  # Fri 23
    h1._persist(force=True)
    h2 = TrafficHeatmap(cache_path=path)
    assert h2.grid == h1.grid
    # last_day must roundtrip too — otherwise the first observation after
    # a restart would wrongly reset the cell.
    assert h2.last_day == h1.last_day


def test_legacy_cache_without_last_day_preserves_counts(tmp_path: Path):
    """A cache written by the pre-rollover code only had a `grid` key.
    The loader should keep those counts visible and stamp populated cells
    with today's day number so the first new observation in that same
    slot today accumulates rather than triggering a spurious reset."""
    import time as time_mod

    path = tmp_path / "heatmap.json"
    legacy = [[0] * HOURS for _ in range(DAYS)]
    legacy[0][12] = 5  # Mon 12 had 5 hits historically
    legacy[3][7] = 0  # empty slot stays at last_day=0
    path.write_text(json.dumps({"grid": legacy}))
    h = TrafficHeatmap(cache_path=path)
    today = int(time_mod.time() // 86400)
    assert h.grid[0][12] == 5
    assert h.last_day[0][12] == today  # populated cell → stamped as today
    assert h.last_day[3][7] == 0  # empty cell → stays at zero


def test_bad_cache_file_is_ignored(tmp_path: Path):
    path = tmp_path / "heatmap.json"
    path.write_text("not json")
    h = TrafficHeatmap(cache_path=path)
    assert h.snapshot()["total"] == 0


def test_short_grid_rows_are_padded(tmp_path: Path):
    """Older caches might have fewer than 24 hours per row; load should pad
    them rather than crash or mis-index."""
    path = tmp_path / "heatmap.json"
    short_grid = [[1] * 12 for _ in range(DAYS)]  # 12 hours instead of 24
    path.write_text(json.dumps({"grid": short_grid}))
    h = TrafficHeatmap(cache_path=path)
    assert len(h.grid) == DAYS
    assert all(len(row) == HOURS for row in h.grid)
    # First 12 hours kept, remainder zeroed.
    assert h.grid[0][0] == 1
    assert h.grid[0][11] == 1
    assert h.grid[0][12] == 0


def test_malformed_grid_does_not_replace_defaults(tmp_path: Path):
    path = tmp_path / "heatmap.json"
    # 'grid' is a list but contains non-lists — loader should bail.
    path.write_text(json.dumps({"grid": ["not a row"] * DAYS}))
    h = TrafficHeatmap(cache_path=path)
    # Fresh defaults retained.
    assert sum(sum(row) for row in h.grid) == 0


def test_maybe_persist_throttles_within_interval(tmp_path: Path):
    """Once a persist has landed, a further dirty observation inside the
    interval is held back (stays `_dirty=True`) rather than rewriting the
    file on every snapshot tick."""
    path = tmp_path / "heatmap.json"
    h = TrafficHeatmap(cache_path=path)
    h.observe(_ts(2024, 1, 1, 12))
    h.maybe_persist(interval=60.0)  # first dirty write goes through
    assert path.exists()
    assert h._dirty is False
    # Second observation dirties the grid again; within the interval the
    # throttle must hold the write back.
    h.observe(_ts(2024, 1, 1, 13))
    assert h._dirty is True
    h.maybe_persist(interval=3600.0)
    assert h._dirty is True  # still pending, not flushed
