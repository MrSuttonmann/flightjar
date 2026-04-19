"""Tests for the adsbdb flight-routes client."""

import time
from pathlib import Path
from unittest.mock import AsyncMock, patch

from app.flight_routes import (
    CACHE_NEGATIVE_TTL,
    CACHE_POSITIVE_TTL,
    CACHE_SCHEMA_VERSION,
    AdsbdbClient,
)


def _client(tmp_path: Path, enabled: bool = True) -> AdsbdbClient:
    return AdsbdbClient(
        cache_path=tmp_path / "flight_routes.json.gz",
        enabled=enabled,
    )


def test_disabled_flag_is_respected(tmp_path: Path):
    c = _client(tmp_path, enabled=False)
    assert c.enabled is False


def test_lookup_returns_none_for_empty_callsign(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    assert asyncio.run(c.lookup("")) is None
    assert asyncio.run(c.lookup("   ")) is None


def test_cache_hit_skips_upstream(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    fake = AsyncMock(return_value={"origin": "EGLL", "destination": "KJFK", "callsign": "BAW1"})
    with patch.object(c, "_fetch", fake):
        first = asyncio.run(c.lookup("BAW1"))
        second = asyncio.run(c.lookup("BAW1"))
    assert first == fake.return_value
    assert second == fake.return_value
    assert fake.await_count == 1


def test_callsign_key_is_normalised(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    fake = AsyncMock(return_value={"origin": "EGLL", "destination": "KJFK", "callsign": "BAW1"})
    with patch.object(c, "_fetch", fake):
        asyncio.run(c.lookup("  baw1 "))
        asyncio.run(c.lookup("BAW1"))
    # Trim + upper-case should collapse both queries onto the same cache key.
    assert fake.await_count == 1
    assert "BAW1" in c._cache


def test_negative_result_is_cached_shorter(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    with patch.object(c, "_fetch", AsyncMock(return_value=None)):
        asyncio.run(c.lookup("UNKNWN"))
    entry = c._cache["UNKNWN"]
    assert entry["expires_at"] - time.time() <= CACHE_NEGATIVE_TTL
    assert entry["expires_at"] - time.time() < CACHE_POSITIVE_TTL


def test_upstream_failure_falls_back_to_stale_cache(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    c._cache["UAL1"] = {
        "data": {"origin": "KSFO", "destination": "PHNL", "callsign": "UAL1"},
        "expires_at": 0,  # stale
    }
    with patch.object(c, "_fetch", AsyncMock(side_effect=RuntimeError("boom"))):
        result = asyncio.run(c.lookup("UAL1"))
    assert result == {"origin": "KSFO", "destination": "PHNL", "callsign": "UAL1"}


def test_cache_persists_and_loads(tmp_path: Path):
    import asyncio

    path = tmp_path / "flight_routes.json.gz"
    c1 = AdsbdbClient(cache_path=path)
    with patch.object(
        c1,
        "_fetch",
        AsyncMock(return_value={"origin": "LFPG", "destination": "RJTT", "callsign": "AFR1"}),
    ):
        asyncio.run(c1.lookup("AFR1"))

    c2 = AdsbdbClient(cache_path=path)
    assert "AFR1" in c2._cache
    assert c2._cache["AFR1"]["data"]["origin"] == "LFPG"


def test_429_triggers_cooldown_and_suppresses_further_requests(tmp_path: Path):
    import asyncio

    import httpx

    c = _client(tmp_path)
    request = httpx.Request("GET", "https://example/test")
    response = httpx.Response(429, request=request)
    err = httpx.HTTPStatusError("rate limited", request=request, response=response)
    fetch = AsyncMock(side_effect=err)
    with patch.object(c, "_fetch", fetch):
        r1 = asyncio.run(c.lookup("BAW1"))
        r2 = asyncio.run(c.lookup("EZY2"))
    assert r1 is None
    assert r2 is None
    assert fetch.await_count == 1
    assert c._cooldown_until > time.time()


def test_429_respects_retry_after_header(tmp_path: Path):
    import asyncio

    import httpx

    c = _client(tmp_path)
    request = httpx.Request("GET", "https://example/test")
    response = httpx.Response(429, headers={"retry-after": "30"}, request=request)
    err = httpx.HTTPStatusError("rate limited", request=request, response=response)
    with patch.object(c, "_fetch", AsyncMock(side_effect=err)):
        asyncio.run(c.lookup("BAW1"))
    delta = c._cooldown_until - time.time()
    assert 25 < delta < 35


def test_expired_entries_are_dropped_on_reload(tmp_path: Path):
    import gzip
    import json

    path = tmp_path / "flight_routes.json.gz"
    with gzip.open(path, "wt", encoding="utf-8") as fh:
        json.dump(
            {
                "version": CACHE_SCHEMA_VERSION,
                "cache": {
                    "STALE": {"data": {"origin": "X"}, "expires_at": 0},
                    "FRESH": {
                        "data": {"origin": "Y"},
                        "expires_at": time.time() + 3600,
                    },
                },
            },
            fh,
        )
    c = AdsbdbClient(cache_path=path)
    assert "STALE" not in c._cache
    assert "FRESH" in c._cache


def test_old_schema_cache_is_ignored(tmp_path: Path):
    """Previous OpenSky-era cache files had no version marker and used
    lower-case ICAO24 keys. They should be silently discarded."""
    import gzip
    import json

    path = tmp_path / "flight_routes.json.gz"
    with gzip.open(path, "wt", encoding="utf-8") as fh:
        json.dump(
            {
                "cache": {
                    "deadbe": {
                        "data": {"origin": "LFPG", "destination": "RJTT"},
                        "expires_at": time.time() + 3600,
                    },
                }
            },
            fh,
        )
    c = AdsbdbClient(cache_path=path)
    assert c._cache == {}
