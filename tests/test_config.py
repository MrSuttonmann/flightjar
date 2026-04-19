"""Tests for typed config parsing and validation."""

import pytest

from app.config import Config, ConfigError


def test_defaults_when_env_is_empty():
    cfg = Config.from_env({})
    assert cfg.beast_host == "readsb"
    assert cfg.beast_port == 30005
    assert cfg.lat_ref is None
    assert cfg.lon_ref is None
    assert cfg.receiver_anon_km == 0.0
    assert cfg.site_name is None
    assert cfg.jsonl_rotate == "daily"
    assert cfg.jsonl_keep == 14
    assert cfg.jsonl_stdout is False
    assert cfg.snapshot_interval == 1.0


def test_parses_happy_path():
    cfg = Config.from_env(
        {
            "BEAST_HOST": "ultrafeeder",
            "BEAST_PORT": "31005",
            "LAT_REF": "52.98",
            "LON_REF": "-1.20",
            "RECEIVER_ANON_KM": "10",
            "SITE_NAME": "Home",
            "BEAST_OUTFILE": "/tmp/x.jsonl",
            "BEAST_ROTATE": "hourly",
            "BEAST_ROTATE_KEEP": "7",
            "BEAST_STDOUT": "1",
            "SNAPSHOT_INTERVAL": "2.5",
        }
    )
    assert cfg.beast_host == "ultrafeeder"
    assert cfg.beast_port == 31005
    assert cfg.lat_ref == 52.98
    assert cfg.lon_ref == -1.20
    assert cfg.receiver_anon_km == 10.0
    assert cfg.site_name == "Home"
    assert cfg.jsonl_path == "/tmp/x.jsonl"
    assert cfg.jsonl_rotate == "hourly"
    assert cfg.jsonl_keep == 7
    assert cfg.jsonl_stdout is True
    assert cfg.snapshot_interval == 2.5


def test_invalid_rotate_raises():
    with pytest.raises(ConfigError, match="BEAST_ROTATE"):
        Config.from_env({"BEAST_ROTATE": "weekly"})


def test_invalid_port_raises():
    with pytest.raises(ConfigError, match="BEAST_PORT"):
        Config.from_env({"BEAST_PORT": "70000"})


def test_non_integer_port_raises():
    with pytest.raises(ConfigError, match="BEAST_PORT"):
        Config.from_env({"BEAST_PORT": "abc"})


def test_negative_rotate_keep_raises():
    with pytest.raises(ConfigError, match="BEAST_ROTATE_KEEP"):
        Config.from_env({"BEAST_ROTATE_KEEP": "-1"})


def test_aircraft_db_refresh_hours_default_is_zero():
    cfg = Config.from_env({})
    assert cfg.aircraft_db_refresh_hours == 0.0


def test_aircraft_db_refresh_hours_accepts_positive_value():
    cfg = Config.from_env({"AIRCRAFT_DB_REFRESH_HOURS": "168"})
    assert cfg.aircraft_db_refresh_hours == 168.0


def test_aircraft_db_refresh_hours_rejects_negative():
    with pytest.raises(ConfigError, match="AIRCRAFT_DB_REFRESH_HOURS"):
        Config.from_env({"AIRCRAFT_DB_REFRESH_HOURS": "-1"})


def test_zero_snapshot_interval_raises():
    with pytest.raises(ConfigError, match="SNAPSHOT_INTERVAL"):
        Config.from_env({"SNAPSHOT_INTERVAL": "0"})


def test_malformed_lat_ref_is_tolerated():
    # Optional floats: malformed input silently becomes None.
    cfg = Config.from_env({"LAT_REF": "not-a-number"})
    assert cfg.lat_ref is None


def test_empty_site_name_treated_as_unset():
    cfg = Config.from_env({"SITE_NAME": "  "})
    assert cfg.site_name is None


def test_stdout_truthy_values():
    for v in ("1", "true", "yes", "on", "TRUE"):
        assert Config.from_env({"BEAST_STDOUT": v}).jsonl_stdout is True
    for v in ("0", "false", "no", "off", ""):
        assert Config.from_env({"BEAST_STDOUT": v}).jsonl_stdout is False
