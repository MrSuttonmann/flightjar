"""Tests for the server-side watchlist store."""

import json
from pathlib import Path

from app.watchlist import WatchlistStore


def test_empty_store_round_trip(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    s = WatchlistStore(path=path)
    assert s.get() == []
    assert len(s) == 0
    assert "abc123" not in s


def test_replace_persists_and_reloads(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    s1 = WatchlistStore(path=path)
    s1.replace(["ABC123", "def456", "  4CA2D1  "])
    # Re-open from disk — all three survived, lowercased + normalised.
    s2 = WatchlistStore(path=path)
    assert s2.get() == ["4ca2d1", "abc123", "def456"]


def test_replace_drops_invalid_entries(tmp_path: Path):
    s = WatchlistStore(path=tmp_path / "watchlist.json")
    s.replace(["abc123", "", "toolong", "XYZ!@#", "00aabb", None, 42])
    assert s.get() == ["00aabb", "abc123"]


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
    # No rewrite = identical mtime. Giving nanosecond tolerance in case
    # the filesystem is weird; main point is the contents didn't change.
    assert mtime1 == mtime2
    assert s.get() == ["abc123"]


def test_replace_dedups(tmp_path: Path):
    s = WatchlistStore(path=tmp_path / "watchlist.json")
    s.replace(["abc123", "ABC123", " abc123 "])
    assert s.get() == ["abc123"]


def test_corrupt_file_is_ignored(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    path.write_text("not valid json", encoding="utf-8")
    s = WatchlistStore(path=path)
    assert s.get() == []


def test_unexpected_schema_is_ignored(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    # Array at top-level is the old (or some third-party) shape;
    # WatchlistStore expects {"version": ..., "icao24s": [...]}.
    path.write_text(json.dumps(["abc123"]), encoding="utf-8")
    s = WatchlistStore(path=path)
    assert s.get() == []


def test_missing_icao24s_key_is_ignored(tmp_path: Path):
    path = tmp_path / "watchlist.json"
    path.write_text(json.dumps({"version": 1, "other": []}), encoding="utf-8")
    s = WatchlistStore(path=path)
    assert s.get() == []


def test_none_path_disables_persistence(tmp_path: Path):
    # Path-less store is valid — just an in-memory set.
    s = WatchlistStore(path=None)
    s.replace(["abc123"])
    assert s.get() == ["abc123"]
    # No file was written (obviously — no path).
    assert list(tmp_path.iterdir()) == []
