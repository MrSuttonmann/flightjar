"""Tests for the OpenFlights airline-DB loader + callsign-prefix lookup."""

from pathlib import Path

import pytest

from app.airlines_db import ALLIANCES, AirlinesDB


@pytest.fixture
def fixture_db(tmp_path: Path) -> Path:
    """A tiny OpenFlights-shaped CSV covering the cases we care about.

    OpenFlights uses `\\N` for NULLs and `-` / `N/A` as "no code" sentinels;
    rows with `Active == "N"` or no ICAO code should be skipped entirely.
    """
    rows = [
        # Active, real carriers (keep).
        '1355,"British Airways",\\N,"BA","BAW","SPEEDBIRD","United Kingdom","Y"',
        '137,"Air France",\\N,"AF","AFR","AIRFRANS","France","Y"',
        '5209,"United Airlines",\\N,"UA","UAL","UNITED","United States","Y"',
        # Sentinel codes — should be dropped.
        '-1,"Unknown",\\N,"-","N/A",\\N,\\N,"Y"',
        '1,"Private flight",\\N,"-","N/A","","","Y"',
        # Defunct carrier — should be dropped.
        '2,"135 Airways",\\N,"","GNL","GENERAL","United States","N"',
        # Active but no ICAO code — should be dropped.
        '9000,"Regional Hop",\\N,"RH","","","France","Y"',
        # Edge case: lowercase ICAO input normalises to upper on read.
        '9001,"Test Airline",\\N,"TA","tst","TEST","Nowhere","Y"',
    ]
    path = tmp_path / "airlines.dat"
    path.write_text("\n".join(rows) + "\n", encoding="utf-8")
    return path


def test_loads_only_active_rows_with_real_icao(fixture_db: Path):
    db = AirlinesDB()
    n = db.load_from(fixture_db)
    # Keepers: BAW, AFR, UAL, TST. Dropped: the two sentinel rows, the
    # defunct 135 Airways, and the no-ICAO Regional Hop.
    assert n == 4
    assert "BAW" in db
    assert "AFR" in db
    assert "UAL" in db
    assert "TST" in db
    assert "GNL" not in db  # Active=N
    assert "N/A" not in db  # sentinel


def test_lookup_by_icao_is_case_insensitive(fixture_db: Path):
    db = AirlinesDB()
    db.load_from(fixture_db)
    by_upper = db.lookup_by_icao("BAW")
    by_lower = db.lookup_by_icao("baw")
    assert by_upper == by_lower
    assert by_upper["name"] == "British Airways"
    assert by_upper["iata"] == "BA"


def test_lookup_by_callsign_strips_flight_number(fixture_db: Path):
    db = AirlinesDB()
    db.load_from(fixture_db)
    entry = db.lookup_by_callsign("BAW123")
    assert entry is not None
    assert entry["icao"] == "BAW"
    assert entry["name"] == "British Airways"


def test_lookup_by_callsign_ignores_numeric_prefixes(fixture_db: Path):
    """Ad-hoc callsigns like '12345' or military tags aren't airlines."""
    db = AirlinesDB()
    db.load_from(fixture_db)
    assert db.lookup_by_callsign("12345") is None
    assert db.lookup_by_callsign("N12345") is None  # GA registration as callsign
    assert db.lookup_by_callsign("") is None
    assert db.lookup_by_callsign(None) is None


def test_alliance_is_attached_when_known(fixture_db: Path):
    db = AirlinesDB()
    db.load_from(fixture_db)
    assert db.lookup_by_icao("BAW")["alliance"] == "oneworld"
    assert db.lookup_by_icao("UAL")["alliance"] == "star"
    assert db.lookup_by_icao("AFR")["alliance"] == "skyteam"
    # Airlines outside the curated alliance dict get alliance=None, not an
    # invented label.
    assert db.lookup_by_icao("TST")["alliance"] is None


def test_missing_file_returns_zero(tmp_path: Path):
    db = AirlinesDB()
    assert db.load_first_available([tmp_path / "nope.dat"]) == 0
    assert len(db) == 0


def test_first_available_picks_the_first_existing(tmp_path: Path, fixture_db: Path):
    db = AirlinesDB()
    db.load_first_available([tmp_path / "nope.dat", fixture_db])
    assert len(db) == 4


def test_load_from_replaces_existing_entries(tmp_path: Path, fixture_db: Path):
    path2 = tmp_path / "db2.dat"
    path2.write_text('1,"New Airline",\\N,"NA","NEW","NEWEST","Nowhere","Y"\n')
    db = AirlinesDB()
    db.load_from(fixture_db)
    assert "BAW" in db
    db.load_from(path2)
    assert "BAW" not in db
    assert "NEW" in db
    assert len(db) == 1


def test_alliance_dict_values_are_canonical():
    """ALLIANCES values must be one of the three strings the frontend
    maps to CSS classes (`alliance-star`, etc.)."""
    allowed = {"star", "oneworld", "skyteam"}
    for code, alliance in ALLIANCES.items():
        assert alliance in allowed, f"{code} has invalid alliance {alliance!r}"
