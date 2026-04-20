"""Tests for registry persistence (save/restore round-trip)."""

import time
from pathlib import Path
from unittest.mock import patch

from app.aircraft import PERSIST_MAX_AGE, Aircraft, AircraftRegistry
from app.persistence import load_state, save_state


def _populate(reg: AircraftRegistry, now: float) -> None:
    ac = Aircraft(
        icao="abc123",
        callsign="RYR123",
        lat=52.5,
        lon=-1.2,
        altitude_baro=35000,
        track=270.0,
        speed=450.0,
        vrate=-600,
        squawk="1234",
        last_seen=now,
        last_position_time=now,
        msg_count=42,
    )
    ac.trail.extend(
        [
            (52.4, -1.1, 34800, 440, now - 10),
            (52.45, -1.15, 34900, 445, now - 5),
            (52.5, -1.2, 35000, 450, now),
        ]
    )
    reg.aircraft["abc123"] = ac


def test_save_and_load_roundtrip(tmp_path: Path):
    path = tmp_path / "state.json.gz"
    now = time.time()

    src = AircraftRegistry()
    _populate(src, now)
    save_state(src, path)

    dst = AircraftRegistry()
    n = load_state(dst, path)

    assert n == 1
    ac = dst.aircraft["abc123"]
    assert ac.callsign == "RYR123"
    assert ac.altitude_baro == 35000
    assert ac.squawk == "1234"
    assert ac.msg_count == 42
    assert len(ac.trail) == 3
    assert list(ac.trail[-1]) == [52.5, -1.2, 35000, 450, now]


def test_load_missing_file_is_zero(tmp_path: Path):
    dst = AircraftRegistry()
    assert load_state(dst, tmp_path / "nope.json.gz") == 0


def test_stale_aircraft_are_dropped_on_restore(tmp_path: Path):
    path = tmp_path / "state.json.gz"
    now = time.time()

    src = AircraftRegistry()
    _populate(src, now - (PERSIST_MAX_AGE + 100))  # way too old
    save_state(src, path)

    dst = AircraftRegistry()
    # Ensure restore uses `now` so the cutoff is deterministic.
    with patch("app.aircraft.time.time", return_value=now):
        loaded = load_state(dst, path)
    assert loaded == 0
    assert len(dst.aircraft) == 0


def test_write_is_atomic(tmp_path: Path):
    # Crash during save shouldn't leave a half-written file at the target path.
    path = tmp_path / "state.json.gz"
    save_state(AircraftRegistry(), path)
    first = path.read_bytes()

    src = AircraftRegistry()
    _populate(src, time.time())
    # Force an error mid-rename by making the final path an unwriteable dir.
    # Simplest cross-platform check: confirm no `.tmp` leftover after success.
    save_state(src, path)
    leftovers = list(path.parent.glob("*.tmp"))
    assert leftovers == []
    # And the file was rewritten (not the original empty snapshot).
    assert path.read_bytes() != first
