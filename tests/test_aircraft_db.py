"""Tests for the aircraft DB loader."""

import gzip
from pathlib import Path

import pytest

from app.aircraft_db import AircraftDB


@pytest.fixture
def fixture_db(tmp_path: Path) -> Path:
    """Create a tiny gzipped DB shaped like tar1090-db's aircraft.csv.gz."""
    path = tmp_path / "aircraft_db.csv.gz"
    rows = [
        "004002;Z-WPA;B732;00;BOEING 737-200;;;",
        "00400C;Z-FAA;B735;00;BOEING 737-500;;;",
        "ABCDEF;;A320;00;AIRBUS A320;;;",  # no registration
        "GARBAGE",  # malformed row, should be skipped
        ";;;;;;;",  # empty row, should be skipped
        "4CA2D1;EI-DYM;B738;00;BOEING 737-800;;;",
    ]
    with gzip.open(path, "wt", encoding="utf-8") as fh:
        fh.write("\n".join(rows))
    return path


def test_loads_expected_rows(fixture_db: Path):
    db = AircraftDB()
    n = db.load_from(fixture_db)
    assert n == 4
    assert len(db) == 4


def test_lookup_is_case_insensitive(fixture_db: Path):
    db = AircraftDB()
    db.load_from(fixture_db)
    # Stored hex is lowercased; query with either case.
    assert db.lookup("4CA2D1") == db.lookup("4ca2d1")
    assert db.lookup("4ca2d1")["registration"] == "EI-DYM"


def test_entry_without_registration_still_loads(fixture_db: Path):
    db = AircraftDB()
    db.load_from(fixture_db)
    entry = db.lookup("abcdef")
    assert entry is not None
    assert entry["registration"] is None
    assert entry["type_icao"] == "A320"


def test_missing_file_returns_zero(tmp_path: Path):
    db = AircraftDB()
    n = db.load_first_available([tmp_path / "nope.csv.gz"])
    assert n == 0
    assert len(db) == 0


def test_first_available_picks_the_first_existing(tmp_path: Path, fixture_db: Path):
    db = AircraftDB()
    db.load_first_available([tmp_path / "nope.csv.gz", fixture_db])
    assert len(db) == 4


def test_load_from_replaces_existing_entries(tmp_path: Path, fixture_db: Path):
    # A second load shouldn't accumulate old entries; swap semantics should
    # leave only what's in the latest file.
    path2 = tmp_path / "db2.csv.gz"
    import gzip as _gzip

    with _gzip.open(path2, "wt", encoding="utf-8") as fh:
        fh.write("FFFFFF;Z-NEW;B744;00;BOEING 747-400;;;\n")
    db = AircraftDB()
    db.load_from(fixture_db)
    assert "4ca2d1" in db
    db.load_from(path2)
    assert "4ca2d1" not in db
    assert "ffffff" in db
    assert len(db) == 1
