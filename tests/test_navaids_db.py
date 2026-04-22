"""Tests for the OurAirports navaids loader + bbox query."""

from pathlib import Path

from app.navaids_db import NavaidsDB


def _fixture(tmp_path: Path) -> Path:
    header = (
        "id,filename,ident,name,type,frequency_khz,latitude_deg,longitude_deg,"
        "elevation_ft,iso_country,dme_frequency_khz,dme_channel,"
        "dme_latitude_deg,dme_longitude_deg,dme_elevation_ft,"
        "slaved_variation_deg,magnetic_variation_deg,usageType,power,"
        "associated_airport\n"
    )
    rows = [
        # DTY — Daventry VOR-DME (UK, a navaid VORs-obsessives recognise).
        "1,DTY_GB,DTY,DAVENTRY,VOR-DME,116400,52.18,-1.11,,GB,,,,,,,,BOTH,HIGH,\n",
        # NDB — Brookmans Park (LON area).
        "2,BPK_GB,BPK,BROOKMANS PARK,NDB,328000,51.75,-0.11,,GB,,,,,,,,BOTH,MEDIUM,\n",
        # Out of the UK bbox: JFK VORTAC.
        "3,JFK_US,JFK,KENNEDY,VORTAC,115900,40.63,-73.77,,US,,,,,,,,BOTH,HIGH,KJFK\n",
        # Skipped type (glideslope).
        "4,IGSX,IGSX,Glideslope,GS,335000,51.47,-0.45,,GB,,,,,,,,BOTH,LOW,EGLL\n",
        # Skipped: no ident.
        "5,NOIDENT,,No Ident,VOR,110000,51.0,0.0,,GB,,,,,,,,,,\n",
        # Skipped: bad coords.
        "6,BAD,BAD,Bad Coords,NDB,,,,GB,,,,,,,,,,\n",
    ]
    path = tmp_path / "navaids.csv"
    path.write_text(header + "".join(rows), encoding="utf-8")
    return path


def test_loads_only_supported_types(tmp_path: Path):
    db = NavaidsDB()
    n = db.load_from(_fixture(tmp_path))
    # DTY + BPK + JFK kept; GS / no-ident / bad-coords skipped.
    assert n == 3


def test_bbox_returns_navaids_in_view(tmp_path: Path):
    db = NavaidsDB()
    db.load_from(_fixture(tmp_path))
    hits = db.bbox(50.0, -2.0, 53.0, 1.0)
    idents = {h["ident"] for h in hits}
    assert idents == {"DTY", "BPK"}


def test_bbox_sorts_vor_family_first(tmp_path: Path):
    db = NavaidsDB()
    db.load_from(_fixture(tmp_path))
    hits = db.bbox(-90.0, -180.0, 90.0, 180.0)
    # VORTAC / VOR-DME rank 0 — must precede NDB (rank 3).
    assert hits[0]["type"] in ("VORTAC", "VOR-DME")
    assert hits[-1]["type"] == "NDB"


def test_bbox_limit_truncates(tmp_path: Path):
    db = NavaidsDB()
    db.load_from(_fixture(tmp_path))
    hits = db.bbox(-90.0, -180.0, 90.0, 180.0, limit=1)
    assert len(hits) == 1
    # Truncation keeps the highest priority (VOR family).
    assert hits[0]["type"] in ("VORTAC", "VOR-DME")


def test_frequency_is_float_or_none(tmp_path: Path):
    db = NavaidsDB()
    db.load_from(_fixture(tmp_path))
    by_ident = {r["ident"]: r for r in db.bbox(-90.0, -180.0, 90.0, 180.0)}
    assert by_ident["DTY"]["frequency_khz"] == 116400.0
    # NDB frequency 328000 kHz parses fine.
    assert by_ident["BPK"]["frequency_khz"] == 328000.0


def test_missing_file_returns_zero(tmp_path: Path):
    db = NavaidsDB()
    assert db.load_first_available([tmp_path / "nope.csv"]) == 0
