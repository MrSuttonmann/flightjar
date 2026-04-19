"""Tests for the OurAirports loader + bbox query."""

from pathlib import Path

from app.airports_db import AirportsDB


def _fixture(tmp_path: Path) -> Path:
    # Minimal OurAirports-shaped CSV with a handful of airports.
    header = (
        "id,ident,type,name,latitude_deg,longitude_deg,elevation_ft,continent,"
        "iso_country,iso_region,municipality,scheduled_service,icao_code,"
        "iata_code,gps_code,local_code,home_link,wikipedia_link,keywords\n"
    )
    rows = [
        "1,EGLL,large_airport,Heathrow,51.47,-0.45,,,GB,GB-ENG,London,yes,EGLL,LHR,EGLL,,,,\n",
        "2,KJFK,large_airport,JFK,40.64,-73.78,,,US,US-NY,New York,yes,KJFK,JFK,KJFK,,,,\n",
        "3,EGKB,medium_airport,Biggin Hill,51.33,0.03,,,GB,GB-ENG,Biggin Hill,yes,EGKB,BQH,EGKB,,,,\n",
        "4,00A,heliport,Total RF,40.07,-74.93,,,US,US-PA,Bensalem,no,,,K00A,00A,,,\n",
        "5,BAD,small_airport,Nameless,,,,,XX,XX-XX,,no,,,,,,,\n",
    ]
    path = tmp_path / "airports.csv"
    path.write_text(header + "".join(rows), encoding="utf-8")
    return path


def test_loads_only_supported_types(tmp_path: Path):
    db = AirportsDB()
    n = db.load_from(_fixture(tmp_path))
    # Heliport and coord-less small airport are skipped.
    assert n == 3
    assert "EGLL" in db
    assert "KJFK" in db
    assert "EGKB" in db
    assert "00A" not in db
    assert "BAD" not in db


def test_lookup_is_case_insensitive(tmp_path: Path):
    db = AirportsDB()
    db.load_from(_fixture(tmp_path))
    assert db.lookup("egll") == db.lookup("EGLL")
    assert db.lookup("EGLL")["name"] == "Heathrow"


def test_bbox_returns_airports_in_view(tmp_path: Path):
    db = AirportsDB()
    db.load_from(_fixture(tmp_path))
    # UK-ish bbox — should hit both EGLL and EGKB but not JFK.
    hits = db.bbox(50.0, -2.0, 52.0, 1.0)
    idents = {h["icao"] for h in hits}
    assert idents == {"EGLL", "EGKB"}


def test_bbox_sorts_big_airports_first(tmp_path: Path):
    db = AirportsDB()
    db.load_from(_fixture(tmp_path))
    hits = db.bbox(50.0, -2.0, 52.0, 1.0)
    # EGLL (large) must come before EGKB (medium) regardless of insertion order.
    assert hits[0]["icao"] == "EGLL"
    assert hits[1]["icao"] == "EGKB"


def test_bbox_limit_truncates(tmp_path: Path):
    db = AirportsDB()
    db.load_from(_fixture(tmp_path))
    hits = db.bbox(-90.0, -180.0, 90.0, 180.0, limit=1)
    assert len(hits) == 1
    # Limit truncation keeps the highest priority.
    assert hits[0]["type"] == "large_airport"


def test_missing_file_returns_zero(tmp_path: Path):
    db = AirportsDB()
    assert db.load_first_available([tmp_path / "nope.csv"]) == 0
