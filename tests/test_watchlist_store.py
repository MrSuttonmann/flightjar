"""Tests for the server-side watchlist store."""

import json
from pathlib import Path
from unittest.mock import patch

from app.watchlist import WatchlistStore


def test_empty_store_round_trip(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    s = WatchlistStore(path=path)
    assert s.icao24s() == []
    assert s.get() == {"icao24s": [], "last_seen": {}}
    assert len(s) == 0
    assert "abc123" not in s


def test_replace_persists_and_reloads(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    s1 = WatchlistStore(path=path)
    s1.replace(["ABC123", "def456", "  4CA2D1  "])
    s2 = WatchlistStore(path=path)
    assert s2.icao24s() == ["4ca2d1", "abc123", "def456"]


def test_replace_drops_invalid_entries(tmp_path: Path):
    s = WatchlistStore(path=tmp_path / "watchlist.json")
    s.replace(["abc123", "", "toolong", "XYZ!@#", "00aabb", None, 42])
    assert s.icao24s() == ["00aabb", "abc123"]


def test_has_is_case_insensitive(tmp_path: Path):
    s = WatchlistStore(path=tmp_path / "watchlist.json")
    s.replace(["abc123"])
    assert "ABC123" in s
    assert "abc123" in s
    assert s.has("AbC123")
    assert not s.has("def456")


def test_replace_is_idempotent(tmp_path: Path):
    """Replacing with the same set shouldn't rewrite the file."""
    path = tmp_path / "watchlist.json"
    s = WatchlistStore(path=path)
    s.replace(["abc123"])
    mtime1 = path.stat().st_mtime_ns
    s.replace(["ABC123"])  # semantically identical
    mtime2 = path.stat().st_mtime_ns
    assert mtime1 == mtime2
    assert s.icao24s() == ["abc123"]


def test_replace_dedups(tmp_path: Path):
    s = WatchlistStore(path=tmp_path / "watchlist.json")
    s.replace(["abc123", "ABC123", " abc123 "])
    assert s.icao24s() == ["abc123"]


def test_corrupt_file_is_ignored(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    path.write_text("not valid json", encoding="utf-8")
    s = WatchlistStore(path=path)
    assert s.icao24s() == []


def test_unexpected_schema_is_ignored(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    path.write_text(json.dumps(["abc123"]), encoding="utf-8")
    s = WatchlistStore(path=path)
    assert s.icao24s() == []


def test_missing_icao24s_key_is_ignored(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    path.write_text(json.dumps({"version": 1, "other": []}), encoding="utf-8")
    s = WatchlistStore(path=path)
    assert s.icao24s() == []


def test_none_path_disables_persistence(tmp_path: Path):
    s = WatchlistStore(path=None)
    s.replace(["abc123"])
    assert s.icao24s() == ["abc123"]
    assert list(tmp_path.iterdir()) == []


# -------- last-seen --------


def test_record_seen_ignores_non_watchlisted(tmp_path: Path):
    s = WatchlistStore(path=tmp_path / "watchlist.json")
    s.replace(["abc123"])
    s.record_seen("def456", 1_700_000_000.0)
    assert s.get()["last_seen"] == {}


def test_record_seen_accepts_case_and_advances(tmp_path: Path):
    s = WatchlistStore(path=tmp_path / "watchlist.json")
    s.replace(["abc123"])
    s.record_seen("ABC123", 1_700_000_000.0)
    s.record_seen("abc123", 1_700_000_060.0)
    assert s.get()["last_seen"] == {"abc123": 1_700_000_060.0}


def test_record_seen_ignores_time_travel(tmp_path: Path):
    s = WatchlistStore(path=tmp_path / "watchlist.json")
    s.replace(["abc123"])
    s.record_seen("abc123", 1_700_000_060.0)
    s.record_seen("abc123", 1_700_000_000.0)  # older than previous
    assert s.get()["last_seen"] == {"abc123": 1_700_000_060.0}


def test_record_seen_persists_first_sighting_immediately(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    s = WatchlistStore(path=path)
    s.replace(["abc123"])
    # File already exists from the replace above but should have an
    # empty last_seen. After the first-ever record_seen the on-disk
    # copy must carry the timestamp — proving we didn't stash it in
    # memory and hit the debounce path.
    assert json.loads(path.read_text())["last_seen"] == {}
    s.record_seen("abc123", 1_700_000_000.0)
    data = json.loads(path.read_text(encoding="utf-8"))
    assert data["last_seen"] == {"abc123": 1_700_000_000.0}


def test_record_seen_debounces_subsequent_updates(tmp_path: Path):
    """Back-to-back timestamp updates for the same icao don't thrash
    the disk — the first write goes through, the next one stays
    in-memory until the debounce window elapses."""
    path = tmp_path / "watchlist.json"
    s = WatchlistStore(path=path)
    s.replace(["abc123"])
    s.record_seen("abc123", 1_700_000_000.0)
    mtime_first = path.stat().st_mtime_ns
    # Simulate an immediately-following update by freezing time at the
    # same instant as the first persist.
    with patch("app.watchlist.time.time", return_value=s._last_persist_ts):
        s.record_seen("abc123", 1_700_000_005.0)
    mtime_second = path.stat().st_mtime_ns
    assert mtime_second == mtime_first
    # In-memory copy did advance, though.
    assert s.get()["last_seen"] == {"abc123": 1_700_000_005.0}


def test_flush_forces_disk_write(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    s = WatchlistStore(path=path)
    s.replace(["abc123"])
    s.record_seen("abc123", 1_700_000_000.0)
    # Advance in-memory only (debounced).
    with patch("app.watchlist.time.time", return_value=s._last_persist_ts):
        s.record_seen("abc123", 1_700_000_005.0)
    # flush() bypasses the debounce.
    s.flush()
    data = json.loads(path.read_text(encoding="utf-8"))
    assert data["last_seen"] == {"abc123": 1_700_000_005.0}


def test_replace_prunes_last_seen_for_removed_entries(tmp_path: Path):
    s = WatchlistStore(path=tmp_path / "watchlist.json")
    s.replace(["abc123", "def456"])
    s.record_seen("abc123", 1_700_000_000.0)
    s.record_seen("def456", 1_700_000_100.0)
    # Drop def456 from the watchlist — its last-seen entry should go too.
    s.replace(["abc123"])
    assert s.get()["last_seen"] == {"abc123": 1_700_000_000.0}


def test_v1_schema_loads_without_last_seen(tmp_path: Path):
    """Pre-existing v1 files (icao24s only, no last_seen key) still
    load cleanly — the last_seen map just starts empty."""
    path = tmp_path / "watchlist.json"
    path.write_text(
        json.dumps({"version": 1, "icao24s": ["abc123"]}),
        encoding="utf-8",
    )
    s = WatchlistStore(path=path)
    assert s.icao24s() == ["abc123"]
    assert s.get()["last_seen"] == {}


def test_last_seen_survives_disk_round_trip(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    s1 = WatchlistStore(path=path)
    s1.replace(["abc123"])
    s1.record_seen("abc123", 1_700_000_000.0)
    # Fresh instance from the same file — the timestamp comes back.
    s2 = WatchlistStore(path=path)
    assert s2.get()["last_seen"] == {"abc123": 1_700_000_000.0}
