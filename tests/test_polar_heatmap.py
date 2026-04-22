"""Tests for the polar (bearing x distance) reception-density heatmap."""

import json
from pathlib import Path

from app.polar_heatmap import BAND_KM, BANDS, BUCKETS, WINDOW_DAYS, PolarHeatmap

# Fixed "now" for tests that don't care about clock progression.
_NOW = 1_700_000_000.0
_TODAY = int(_NOW // 86400)


def test_disabled_without_receiver(tmp_path: Path):
    h = PolarHeatmap(cache_path=tmp_path / "ph.json")
    h.observe(52.5, -1.2, now=_NOW)  # no-op — no receiver location
    snap = h.snapshot(now=_NOW)
    assert snap["total"] == 0
    assert all(c == 0 for row in snap["grid"] for c in row)


def test_counts_by_bearing_and_distance(tmp_path: Path):
    h = PolarHeatmap(receiver_lat=52.0, receiver_lon=-1.0, cache_path=tmp_path / "ph.json")
    # Two fixes due north: one in the first band (~55km), one in the next (~78km).
    h.observe(52.5, -1.0, now=_NOW)
    h.observe(52.7, -1.0, now=_NOW)
    snap = h.snapshot(now=_NOW)
    assert snap["total"] == 2
    # Bucket 0 is 0-10 deg (centre 5 deg), directly north.
    row = snap["grid"][0]
    assert row[int(55 // BAND_KM)] == 1  # band 2 (50-75km)
    assert row[int(78 // BAND_KM)] == 1  # band 3 (75-100km)


def test_far_fixes_fold_into_outer_band(tmp_path: Path):
    h = PolarHeatmap(receiver_lat=52.0, receiver_lon=-1.0, cache_path=tmp_path / "ph.json")
    # ~500 km north — well beyond BANDS * BAND_KM; must land in the last band,
    # not get dropped.
    h.observe(56.5, -1.0, now=_NOW)
    snap = h.snapshot(now=_NOW)
    assert snap["total"] == 1
    assert snap["grid"][0][BANDS - 1] == 1


def test_snapshot_sums_across_window(tmp_path: Path):
    """Observations across multiple days inside the window should all
    contribute to the combined snapshot."""
    h = PolarHeatmap(receiver_lat=52.0, receiver_lon=-1.0, cache_path=tmp_path / "ph.json")
    # 4 observations on 4 consecutive days, all in the same cell.
    for offset in range(4):
        h.observe(52.5, -1.0, now=_NOW + offset * 86400)
    snap = h.snapshot(now=_NOW + 3 * 86400)
    assert snap["total"] == 4


def test_observations_older_than_window_are_pruned(tmp_path: Path):
    """A fix from more than WINDOW_DAYS ago must not appear in the snapshot."""
    h = PolarHeatmap(receiver_lat=52.0, receiver_lon=-1.0, cache_path=tmp_path / "ph.json")
    h.observe(52.5, -1.0, now=_NOW)  # day 0
    h.observe(52.5, -1.0, now=_NOW + 86400)  # day 1
    # Advance past the window — the day-0 observation should drop out.
    snap = h.snapshot(now=_NOW + (WINDOW_DAYS + 1) * 86400)
    # Day 1 is also outside the window by now; both should be gone.
    assert snap["total"] == 0


def test_partial_window_aging(tmp_path: Path):
    """With 3 observations on days 0, 3, and 8, a snapshot on day 8 keeps
    day 3 and day 8 (inside the 7-day window) and drops day 0."""
    h = PolarHeatmap(receiver_lat=52.0, receiver_lon=-1.0, cache_path=tmp_path / "ph.json")
    h.observe(52.5, -1.0, now=_NOW + 0 * 86400)  # day 0
    h.observe(52.5, -1.0, now=_NOW + 3 * 86400)  # day 3
    h.observe(52.5, -1.0, now=_NOW + 8 * 86400)  # day 8
    snap = h.snapshot(now=_NOW + 8 * 86400)
    # WINDOW_DAYS=7 → cutoff = today - 6. On day 8, cutoff=2, so day 0 drops
    # but days 3 and 8 survive.
    assert snap["total"] == 2


def test_persistence_roundtrips(tmp_path: Path):
    path = tmp_path / "ph.json"
    clock = lambda: _NOW  # noqa: E731
    h1 = PolarHeatmap(receiver_lat=52.0, receiver_lon=-1.0, cache_path=path, time_fn=clock)
    h1.observe(52.5, -1.0)
    h1.observe(52.0, 0.0)
    h1._persist(force=True)

    h2 = PolarHeatmap(receiver_lat=52.0, receiver_lon=-1.0, cache_path=path, time_fn=clock)
    assert h1.snapshot()["grid"] == h2.snapshot()["grid"]
    assert h2.snapshot()["total"] == 2


def test_reset_clears_grid_and_disk(tmp_path: Path):
    path = tmp_path / "ph.json"
    h = PolarHeatmap(receiver_lat=52.0, receiver_lon=-1.0, cache_path=path)
    h.observe(52.5, -1.0, now=_NOW)
    assert h.snapshot(now=_NOW)["total"] == 1
    h.reset()
    assert h.snapshot(now=_NOW)["total"] == 0
    data = json.loads(path.read_text())
    assert data["daily_grids"] == {}


def test_bad_cache_file_is_ignored(tmp_path: Path):
    path = tmp_path / "ph.json"
    path.write_text("not json")
    h = PolarHeatmap(receiver_lat=52.0, receiver_lon=-1.0, cache_path=path)
    assert h.snapshot(now=_NOW)["total"] == 0


def test_legacy_cumulative_grid_migrates_into_today(tmp_path: Path):
    """Pre-window caches stored a single cumulative `grid`. Load should
    migrate it into today's slot so it stays visible for one window
    rather than vanishing silently."""
    path = tmp_path / "ph.json"
    legacy = [[0] * BANDS for _ in range(BUCKETS)]
    legacy[0][3] = 5
    path.write_text(json.dumps({"grid": legacy}))
    h = PolarHeatmap(receiver_lat=52.0, receiver_lon=-1.0, cache_path=path, time_fn=lambda: _NOW)
    # Legacy counts are visible immediately after load.
    snap = h.snapshot()
    assert snap["grid"][0][3] == 5


def test_schema_width_mismatch_pads_defensively(tmp_path: Path):
    """A persisted daily grid with fewer bands than the current schema
    should pad with zeros instead of crashing."""
    path = tmp_path / "ph.json"
    short_grid = [[1] * (BANDS - 2) for _ in range(BUCKETS)]
    path.write_text(json.dumps({"daily_grids": {str(_TODAY): short_grid}}))
    h = PolarHeatmap(receiver_lat=52.0, receiver_lon=-1.0, cache_path=path, time_fn=lambda: _NOW)
    snap = h.snapshot()
    assert len(snap["grid"]) == BUCKETS
    for row in snap["grid"]:
        assert len(row) == BANDS
        assert row[: BANDS - 2] == [1] * (BANDS - 2)
        assert row[BANDS - 2 :] == [0, 0]
