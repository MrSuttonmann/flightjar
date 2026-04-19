"""Tests for the AircraftRegistry ingest + snapshot pipeline.

We stub pyModeS so these tests don't depend on the upstream decoder's exact
field naming — they only validate our dispatch, state, and snapshot shaping.
"""

from unittest.mock import patch

from app.aircraft import AIRCRAFT_TIMEOUT, AircraftRegistry


def fake_decode(msg, **kw):
    """Return a canned dict based on a tiny test-only message ID convention.

    Messages are hex strings; we use the first byte as a selector into a table.
    This keeps tests readable without fabricating real DF17 wire bytes.
    """
    selector = msg[:4]
    # reference / surface_ref for local-position tests
    if "reference" in kw or "surface_ref" in kw:
        base = _TABLE.get(selector, {}).copy()
        base.setdefault("latitude", 52.1)
        base.setdefault("longitude", -1.1)
        return base
    return _TABLE.get(selector, {}).copy()


_TABLE = {
    # DF17 identification (typecode 1-4), sets callsign + category
    "ID01": {
        "df": 17,
        "crc_valid": True,
        "icao": "abc123",
        "typecode": 4,
        "callsign": "FLY123__",
        "category": 3,
    },
    # DF17 airborne baro position (typecode 11)
    "AP01": {
        "df": 17,
        "crc_valid": True,
        "icao": "abc123",
        "typecode": 11,
        "altitude": 37000,
        "cpr_format": 0,
    },
    # DF17 airborne GNSS / geometric position (typecode 20)
    "GP01": {
        "df": 17,
        "crc_valid": True,
        "icao": "abc123",
        "typecode": 20,
        "altitude": 37100,
        "cpr_format": 0,
    },
    # DF17 velocity (typecode 19)
    "VL01": {
        "df": 17,
        "crc_valid": True,
        "icao": "abc123",
        "typecode": 19,
        "groundspeed": 450,
        "track": 270.0,
        "vertical_rate": -600,
    },
    # DF4 altitude surveillance reply
    "AC01": {"df": 4, "icao": "def456", "altitude": 24000},
    # DF5 squawk reply
    "SQ01": {"df": 5, "icao": "def456", "squawk": "1234"},
    # DF17 with bad CRC (should be dropped)
    "BAD1": {"df": 17, "crc_valid": False, "icao": "aaa111", "typecode": 1},
}


def test_ingest_adsb_identification_populates_callsign_and_category():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry()
        assert reg.ingest("ID01beefcafe", now=100.0)
    ac = reg.aircraft["abc123"]
    assert ac.callsign == "FLY123"  # trailing underscores stripped
    assert ac.category == 3
    assert ac.msg_count == 1


def test_ingest_df4_altitude_surveillance():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry()
        reg.ingest("AC01xxxx", now=50.0)
    ac = reg.aircraft["def456"]
    assert ac.altitude == 24000
    assert ac.callsign is None


def test_ingest_df5_squawk():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry()
        reg.ingest("SQ01xxxx", now=50.0)
    assert reg.aircraft["def456"].squawk == "1234"


def test_bad_crc_does_not_create_aircraft():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry()
        assert reg.ingest("BAD1xxxx", now=50.0) is False
    assert "aaa111" not in reg.aircraft


def test_velocity_populates_speed_track_vrate():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry()
        reg.ingest("VL01xxxx", now=10.0)
    ac = reg.aircraft["abc123"]
    assert ac.speed == 450
    assert ac.track == 270.0
    assert ac.vrate == -600


def test_local_position_uses_receiver_reference():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry(lat_ref=52.0, lon_ref=-1.0)
        reg.ingest("AP01xxxx", now=10.0)
    ac = reg.aircraft["abc123"]
    assert ac.altitude == 37000
    assert ac.lat == 52.1
    assert ac.lon == -1.1
    assert ac.on_ground is False


def test_snapshot_skips_aircraft_with_only_icao():
    with patch("app.aircraft.pms.decode", side_effect=lambda msg, **kw: {"df": 11, "icao": "ddd"}):
        reg = AircraftRegistry()
        reg.ingest("DF11xxxx", now=1.0)
    snap = reg.snapshot(now=1.5)
    assert snap["count"] == 0
    assert snap["aircraft"] == []


def test_snapshot_includes_callsigned_aircraft():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry()
        reg.ingest("ID01xxxx", now=1.0)
    snap = reg.snapshot(now=1.2)
    assert snap["count"] == 1
    assert snap["aircraft"][0]["callsign"] == "FLY123"


def test_snapshot_distance_km_uses_displayed_receiver():
    # Displayed receiver is the anonymised one; distance must match that.
    receiver = {"lat": 52.0, "lon": -1.0, "anon_km": 10}
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry(lat_ref=52.0, lon_ref=-1.0, receiver_info=receiver)
        reg.ingest("AP01xxxx", now=1.0)
    snap = reg.snapshot(now=1.2)
    ac_entry = snap["aircraft"][0]
    # AP01 resolves to (52.1, -1.1); ~13-14 km from (52.0, -1.0).
    assert 5 < ac_entry["distance_km"] < 20


def test_cleanup_evicts_stale_aircraft():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry()
        reg.ingest("ID01xxxx", now=100.0)
        assert "abc123" in reg.aircraft
        reg.cleanup(now=100.0 + AIRCRAFT_TIMEOUT + 1)
    assert "abc123" not in reg.aircraft


def test_emergency_squawk_populates_snapshot_field():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry()
        # Ingest a callsign to get past the "skip aircraft with nothing" filter,
        # then set the squawk directly (emulates a DF5 follow-up message).
        reg.ingest("ID01xxxx", now=10.0)
        reg.aircraft["abc123"].squawk = "7700"
    snap = reg.snapshot(now=10.1)
    assert snap["aircraft"][0]["emergency"] == "general"


def test_non_emergency_squawk_has_no_emergency_flag():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry()
        reg.ingest("ID01xxxx", now=10.0)
        reg.aircraft["abc123"].squawk = "1200"
    snap = reg.snapshot(now=10.1)
    assert snap["aircraft"][0]["emergency"] is None


def test_baro_altitude_is_stored_separately_from_geo():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry(lat_ref=52.0, lon_ref=-1.0)
        reg.ingest("AP01xxxx", now=10.0)
        reg.ingest("GP01xxxx", now=10.1)
    ac = reg.aircraft["abc123"]
    assert ac.altitude_baro == 37000
    assert ac.altitude_geo == 37100
    # altitude property prefers baro when both are known.
    assert ac.altitude == 37000


def test_geo_only_aircraft_reports_altitude_via_fallback():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry(lat_ref=52.0, lon_ref=-1.0)
        reg.ingest("GP01xxxx", now=10.0)
    ac = reg.aircraft["abc123"]
    assert ac.altitude_baro is None
    assert ac.altitude_geo == 37100
    assert ac.altitude == 37100


def test_mlat_ticks_is_recorded_on_ingest():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry()
        reg.ingest("ID01xxxx", now=1.0, mlat_ticks=1234567890)
    assert reg.aircraft["abc123"].last_seen_mlat == 1234567890


def test_snapshot_exposes_both_altitude_fields():
    with patch("app.aircraft.pms.decode", side_effect=fake_decode):
        reg = AircraftRegistry(lat_ref=52.0, lon_ref=-1.0)
        reg.ingest("AP01xxxx", now=1.0)
        reg.ingest("GP01xxxx", now=1.1)
    snap = reg.snapshot(now=1.2)
    entry = snap["aircraft"][0]
    assert entry["altitude"] == 37000
    assert entry["altitude_baro"] == 37000
    assert entry["altitude_geo"] == 37100


def test_unknown_df_is_ignored():
    with patch("app.aircraft.pms.decode", side_effect=lambda msg, **kw: {"df": 19}):
        reg = AircraftRegistry()
        assert reg.ingest("XXX1", now=1.0) is False
    assert len(reg.aircraft) == 0
