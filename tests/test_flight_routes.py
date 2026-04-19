"""Tests for the OpenSky flight-routes client."""

import time
from pathlib import Path
from unittest.mock import AsyncMock, patch

from app.flight_routes import (
    CACHE_NEGATIVE_TTL,
    CACHE_POSITIVE_TTL,
    OpenSkyClient,
)


def _client(tmp_path: Path, enabled: bool = True) -> OpenSkyClient:
    return OpenSkyClient(
        client_id="id" if enabled else None,
        client_secret="secret" if enabled else None,
        cache_path=tmp_path / "flight_routes.json.gz",
    )


def test_disabled_without_credentials(tmp_path: Path):
    c = _client(tmp_path, enabled=False)
    assert c.enabled is False


def test_enabled_returns_none_for_empty_icao(tmp_path: Path):
    c = _client(tmp_path)
    # Run a coroutine synchronously for a quick smoke test.
    import asyncio

    result = asyncio.run(c.lookup(""))
    assert result is None


def test_cache_hit_skips_upstream(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    fake = AsyncMock(return_value={"origin": "EGLL", "destination": "KJFK", "callsign": "BA1"})
    with patch.object(c, "_fetch", fake):
        first = asyncio.run(c.lookup("abc123"))
        second = asyncio.run(c.lookup("abc123"))
    assert first == fake.return_value
    assert second == fake.return_value
    assert fake.await_count == 1  # cached on the second call


def test_negative_result_is_cached_shorter(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    fake = AsyncMock(return_value=None)
    with patch.object(c, "_fetch", fake):
        asyncio.run(c.lookup("nothing"))
    entry = c._cache["nothing"]
    # Negative entries use CACHE_NEGATIVE_TTL, positive use CACHE_POSITIVE_TTL.
    assert entry["expires_at"] - time.time() <= CACHE_NEGATIVE_TTL
    assert entry["expires_at"] - time.time() < CACHE_POSITIVE_TTL


def test_upstream_failure_falls_back_to_stale_cache(tmp_path: Path):
    import asyncio

    c = _client(tmp_path)
    c._cache["abc123"] = {
        "data": {"origin": "KSFO", "destination": "PHNL", "callsign": "UA1"},
        "expires_at": 0,  # stale
    }
    with patch.object(c, "_fetch", AsyncMock(side_effect=RuntimeError("boom"))):
        result = asyncio.run(c.lookup("abc123"))
    # Upstream failed, but we still have stale data — return it rather than None.
    assert result == {"origin": "KSFO", "destination": "PHNL", "callsign": "UA1"}


def test_cache_persists_and_loads(tmp_path: Path):
    import asyncio

    path = tmp_path / "flight_routes.json.gz"
    c1 = OpenSkyClient(client_id="id", client_secret="s", cache_path=path)
    with patch.object(
        c1,
        "_fetch",
        AsyncMock(return_value={"origin": "LFPG", "destination": "RJTT", "callsign": "AF1"}),
    ):
        asyncio.run(c1.lookup("deadbe"))

    # New client with the same path should pick the entry back up.
    c2 = OpenSkyClient(client_id="id", client_secret="s", cache_path=path)
    assert "deadbe" in c2._cache
    assert c2._cache["deadbe"]["data"]["origin"] == "LFPG"


def test_expired_entries_are_dropped_on_reload(tmp_path: Path):
    import gzip
    import json

    path = tmp_path / "flight_routes.json.gz"
    with gzip.open(path, "wt", encoding="utf-8") as fh:
        json.dump(
            {
                "cache": {
                    "stale": {"data": {"origin": "X"}, "expires_at": 0},
                    "fresh": {
                        "data": {"origin": "Y"},
                        "expires_at": time.time() + 3600,
                    },
                }
            },
            fh,
        )
    c = OpenSkyClient(client_id="id", client_secret="s", cache_path=path)
    assert "stale" not in c._cache
    assert "fresh" in c._cache
