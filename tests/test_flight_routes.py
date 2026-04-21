"""Tests for the adsbdb flight-routes + aircraft client."""

import time
from pathlib import Path
from unittest.mock import AsyncMock, patch

from app.flight_routes import (
    AIRCRAFT_NEGATIVE_TTL,
    AIRCRAFT_POSITIVE_TTL,
    CACHE_SCHEMA_VERSION,
    ROUTE_NEGATIVE_TTL,
    ROUTE_POSITIVE_TTL,
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


def test_route_lookup_returns_none_for_empty_callsign(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    assert asyncio.run(c.lookup_route("")) is None
    assert asyncio.run(c.lookup_route("   ")) is None


def test_route_cache_hit_skips_upstream(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    fake = AsyncMock(return_value={"origin": "EGLL", "destination": "KJFK", "callsign": "BAW1"})
    with patch.object(c, "_fetch_route", fake):
        first = asyncio.run(c.lookup_route("BAW1"))
        second = asyncio.run(c.lookup_route("BAW1"))
    assert first == fake.return_value
    assert second == fake.return_value
    assert fake.await_count == 1


def test_route_callsign_key_is_normalised(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    fake = AsyncMock(return_value={"origin": "EGLL", "destination": "KJFK", "callsign": "BAW1"})
    with patch.object(c, "_fetch_route", fake):
        asyncio.run(c.lookup_route("  baw1 "))
        asyncio.run(c.lookup_route("BAW1"))
    assert fake.await_count == 1
    assert "BAW1" in c._routes


def test_route_negative_is_cached_shorter(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    with patch.object(c, "_fetch_route", AsyncMock(return_value=None)):
        asyncio.run(c.lookup_route("UNKNWN"))
    entry = c._routes["UNKNWN"]
    assert entry["expires_at"] - time.time() <= ROUTE_NEGATIVE_TTL
    assert entry["expires_at"] - time.time() < ROUTE_POSITIVE_TTL


def test_aircraft_lookup_rejects_bad_hex(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    # Non-hex characters
    assert asyncio.run(c.lookup_aircraft("xyz")) is None
    # Too long
    assert asyncio.run(c.lookup_aircraft("0123456")) is None
    # Empty
    assert asyncio.run(c.lookup_aircraft("")) is None


def test_aircraft_cache_hit_skips_upstream(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    payload = {
        "registration": "G-EZAN",
        "type": "A319-111",
        "icao_type": "A319",
        "manufacturer": "Airbus Sas",
        "operator": "easyJet Airline",
        "operator_country": "United Kingdom",
        "operator_country_iso": "GB",
        "photo_url": "https://airport-data.com/images/aircraft/001.jpg",
        "photo_thumbnail": "https://airport-data.com/images/aircraft/thumb/001.jpg",
    }
    fake = AsyncMock(return_value=payload)
    with patch.object(c, "_fetch_aircraft", fake):
        first = asyncio.run(c.lookup_aircraft("400db1"))
        second = asyncio.run(c.lookup_aircraft("400DB1"))  # case-insensitive
    assert first == payload
    assert second == payload
    assert fake.await_count == 1


def test_aircraft_negative_is_cached_shorter(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    with patch.object(c, "_fetch_aircraft", AsyncMock(return_value=None)):
        asyncio.run(c.lookup_aircraft("abc123"))
    entry = c._aircraft["abc123"]
    assert entry["expires_at"] - time.time() <= AIRCRAFT_NEGATIVE_TTL
    assert entry["expires_at"] - time.time() < AIRCRAFT_POSITIVE_TTL


def test_route_and_aircraft_caches_do_not_collide(tmp_path: Path):
    """Same string key across route + aircraft lookups must not cross-pollute."""
    import asyncio

    c = _client(tmp_path)
    # 'ABCDEF' is a valid aircraft hex (lowercased) and also a plausible
    # callsign. Each should land in its own bucket.
    aircraft_payload = {
        "registration": "N123AB",
        "photo_url": None,
        "photo_thumbnail": None,
    }
    route_payload = {"origin": "EGLL", "destination": "KJFK", "callsign": "ABCDEF"}
    with (
        patch.object(c, "_fetch_aircraft", AsyncMock(return_value=aircraft_payload)),
        patch.object(c, "_fetch_route", AsyncMock(return_value=route_payload)),
    ):
        asyncio.run(c.lookup_aircraft("abcdef"))
        asyncio.run(c.lookup_route("ABCDEF"))
    assert "abcdef" in c._aircraft
    assert "ABCDEF" in c._routes
    assert c._aircraft["abcdef"]["data"]["registration"] == "N123AB"
    assert c._routes["ABCDEF"]["data"]["origin"] == "EGLL"


def test_upstream_failure_falls_back_to_stale_cache(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    c._routes["UAL1"] = {
        "data": {"origin": "KSFO", "destination": "PHNL", "callsign": "UAL1"},
        "expires_at": 0,  # stale
    }
    with patch.object(c, "_fetch_route", AsyncMock(side_effect=RuntimeError("boom"))):
        result = asyncio.run(c.lookup_route("UAL1"))
    assert result == {"origin": "KSFO", "destination": "PHNL", "callsign": "UAL1"}


def test_cache_persists_both_buckets_and_loads(tmp_path: Path):
    import asyncio

    path = tmp_path / "flight_routes.json.gz"
    c1 = AdsbdbClient(cache_path=path)
    with (
        patch.object(
            c1,
            "_fetch_route",
            AsyncMock(return_value={"origin": "LFPG", "destination": "RJTT", "callsign": "AFR1"}),
        ),
        patch.object(
            c1,
            "_fetch_aircraft",
            AsyncMock(return_value={"registration": "F-AAAA", "photo_url": None}),
        ),
    ):
        asyncio.run(c1.lookup_route("AFR1"))
        asyncio.run(c1.lookup_aircraft("abcdef"))

    c2 = AdsbdbClient(cache_path=path)
    assert "AFR1" in c2._routes
    assert c2._routes["AFR1"]["data"]["origin"] == "LFPG"
    assert "abcdef" in c2._aircraft
    assert c2._aircraft["abcdef"]["data"]["registration"] == "F-AAAA"


def test_429_cooldown_blocks_both_endpoints(tmp_path: Path):
    """A 429 on a route call should suppress a subsequent aircraft call."""
    import asyncio

    import httpx

    c = _client(tmp_path)
    request = httpx.Request("GET", "https://example/test")
    response = httpx.Response(429, request=request)
    err = httpx.HTTPStatusError("rate limited", request=request, response=response)
    route_fetch = AsyncMock(side_effect=err)
    aircraft_fetch = AsyncMock(return_value={"registration": "G-FOO"})
    with (
        patch.object(c, "_fetch_route", route_fetch),
        patch.object(c, "_fetch_aircraft", aircraft_fetch),
    ):
        asyncio.run(c.lookup_route("BAW1"))  # hits upstream, 429s
        result = asyncio.run(c.lookup_aircraft("abcdef"))  # should be suppressed
    assert route_fetch.await_count == 1
    assert aircraft_fetch.await_count == 0
    assert result is None
    assert c._cooldown_until > time.time()


def test_429_respects_retry_after_header(tmp_path: Path):
    import asyncio

    import httpx

    c = _client(tmp_path)
    request = httpx.Request("GET", "https://example/test")
    response = httpx.Response(429, headers={"retry-after": "30"}, request=request)
    err = httpx.HTTPStatusError("rate limited", request=request, response=response)
    with patch.object(c, "_fetch_route", AsyncMock(side_effect=err)):
        asyncio.run(c.lookup_route("BAW1"))
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
                "routes": {
                    "STALE": {"data": {"origin": "X"}, "expires_at": 0},
                    "FRESH": {
                        "data": {"origin": "Y"},
                        "expires_at": time.time() + 3600,
                    },
                },
                "aircraft": {
                    "expired": {"data": {"registration": "X"}, "expires_at": 0},
                    "current": {
                        "data": {"registration": "Y"},
                        "expires_at": time.time() + 3600,
                    },
                },
            },
            fh,
        )
    c = AdsbdbClient(cache_path=path)
    assert "STALE" not in c._routes
    assert "FRESH" in c._routes
    assert "expired" not in c._aircraft
    assert "current" in c._aircraft


def test_lookup_cached_route_distinguishes_miss_from_negative(tmp_path: Path):
    """build_snapshot relies on the `known` flag to skip a background fill
    when a cached-negative entry is already on file — the old single-None
    return mixed 'never seen' and 'we asked and got nothing' together."""
    import asyncio

    c = _client(tmp_path)
    # Unknown key → known=False, data=None.
    assert c.lookup_cached_route("MISSING") == (False, None)
    # Cached-negative → known=True, data=None.
    with patch.object(c, "_fetch_route", AsyncMock(return_value=None)):
        asyncio.run(c.lookup_route("UNKNWN"))
    assert c.lookup_cached_route("UNKNWN") == (True, None)
    # Cached-positive → known=True, data=dict.
    payload = {"origin": "EGLL", "destination": "KJFK", "callsign": "BAW1"}
    with patch.object(c, "_fetch_route", AsyncMock(return_value=payload)):
        asyncio.run(c.lookup_route("BAW1"))
    assert c.lookup_cached_route("BAW1") == (True, payload)


def test_lookup_cached_aircraft_distinguishes_miss_from_negative(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    assert c.lookup_cached_aircraft("abcdef") == (False, None)
    with patch.object(c, "_fetch_aircraft", AsyncMock(return_value=None)):
        asyncio.run(c.lookup_aircraft("abcdef"))
    assert c.lookup_cached_aircraft("abcdef") == (True, None)


def test_lookup_cached_treats_bad_key_as_miss(tmp_path: Path):
    c = _client(tmp_path)
    # Empty / malformed inputs can't hit the cache at all.
    assert c.lookup_cached_route("") == (False, None)
    assert c.lookup_cached_aircraft("xyz") == (False, None)  # non-hex
    assert c.lookup_cached_aircraft("0123456") == (False, None)  # too long


def test_aclose_releases_shared_client(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    _ = c._client()
    assert c._http is not None
    asyncio.run(c.aclose())
    assert c._http is None


def test_old_schema_cache_is_ignored(tmp_path: Path):
    """v2 cache files (single 'cache' bucket) should be discarded cleanly."""
    import gzip
    import json

    path = tmp_path / "flight_routes.json.gz"
    with gzip.open(path, "wt", encoding="utf-8") as fh:
        json.dump(
            {
                "version": 2,
                "cache": {
                    "BAW1": {
                        "data": {"origin": "EGLL", "destination": "KJFK"},
                        "expires_at": time.time() + 3600,
                    },
                },
            },
            fh,
        )
    c = AdsbdbClient(cache_path=path)
    assert c._routes == {}
    assert c._aircraft == {}
