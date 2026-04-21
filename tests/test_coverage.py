"""Tests for the polar-coverage max-range tracker."""

import json
from pathlib import Path

from app.coverage import BUCKETS, PolarCoverage


def test_disabled_without_receiver(tmp_path: Path):
    c = PolarCoverage(cache_path=tmp_path / "cov.json")
    c.observe(52.5, -1.2)  # no-op — no receiver location
    snap = c.snapshot()
    assert snap["bearings"] == []


def test_records_max_distance_per_bucket(tmp_path: Path):
    c = PolarCoverage(receiver_lat=52.0, receiver_lon=-1.0, cache_path=tmp_path / "cov.json")
    # Two points directly north of the receiver, one further than the other.
    c.observe(52.5, -1.0)  # ~55km
    c.observe(53.0, -1.0)  # ~111km — bigger, wins the bucket
    c.observe(52.7, -1.0)  # smaller, should NOT overwrite
    snap = c.snapshot()
    # Pick the bearing bucket closest to 0° (north).
    north_entries = [b for b in snap["bearings"] if b["angle"] < 10.0 or b["angle"] > 350.0]
    assert len(north_entries) == 1
    assert 105 < north_entries[0]["dist_km"] < 115


def test_persistence_roundtrips(tmp_path: Path):
    path = tmp_path / "cov.json"
    c1 = PolarCoverage(receiver_lat=52.0, receiver_lon=-1.0, cache_path=path)
    c1.observe(53.0, -1.0)  # north, far
    c1.observe(52.0, 0.0)  # east, closer
    c1._persist(force=True)

    # Re-create with the same path; persisted max distances come back.
    c2 = PolarCoverage(receiver_lat=52.0, receiver_lon=-1.0, cache_path=path)
    snap1 = c1.snapshot()
    snap2 = c2.snapshot()
    # Same buckets, same distances.
    assert len(snap2["bearings"]) == len(snap1["bearings"])
    by_angle_1 = {b["angle"]: b["dist_km"] for b in snap1["bearings"]}
    by_angle_2 = {b["angle"]: b["dist_km"] for b in snap2["bearings"]}
    assert by_angle_1 == by_angle_2


def test_reset_clears_all_buckets(tmp_path: Path):
    path = tmp_path / "cov.json"
    c = PolarCoverage(receiver_lat=52.0, receiver_lon=-1.0, cache_path=path)
    c.observe(53.0, -1.0)
    assert c.snapshot()["bearings"]
    c.reset()
    assert c.snapshot()["bearings"] == []
    # Reset persisted too.
    data = json.loads(path.read_text())
    assert data["maxdist"] == [0.0] * BUCKETS


def test_on_new_max_fires_only_when_bucket_grows(tmp_path: Path):
    c = PolarCoverage(receiver_lat=52.0, receiver_lon=-1.0, cache_path=tmp_path / "cov.json")
    events: list[tuple[float, float]] = []
    c.on_new_max = lambda angle, dist: events.append((angle, dist))
    # First observation sets a bucket max — fires.
    c.observe(53.0, -1.0)  # ~111 km north
    assert len(events) == 1
    angle0, dist0 = events[0]
    assert 0.0 <= angle0 < 10.0
    assert 105 < dist0 < 115
    # A closer fix in the same bucket doesn't grow the max — no event.
    c.observe(52.5, -1.0)
    assert len(events) == 1
    # A further fix in the same bucket fires again.
    c.observe(54.0, -1.0)
    assert len(events) == 2


def test_on_new_max_swallows_callback_errors(tmp_path: Path):
    c = PolarCoverage(receiver_lat=52.0, receiver_lon=-1.0, cache_path=tmp_path / "cov.json")

    def boom(_angle: float, _dist: float) -> None:
        raise RuntimeError("nope")

    c.on_new_max = boom
    # Must not propagate — the max should still be set.
    c.observe(53.0, -1.0)
    assert c.maxdist[0] > 0


def test_bad_cache_file_is_ignored(tmp_path: Path):
    path = tmp_path / "cov.json"
    path.write_text("not json")
    c = PolarCoverage(receiver_lat=52.0, receiver_lon=-1.0, cache_path=path)
    # Broken file → starts fresh rather than crashing.
    assert c.snapshot()["bearings"] == []
