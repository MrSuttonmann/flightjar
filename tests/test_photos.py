"""Tests for the planespotters.net photo client."""

import asyncio
import gzip
import json
import time
from pathlib import Path
from unittest.mock import AsyncMock, patch

import httpx

from app.photos import (
    CACHE_SCHEMA_VERSION,
    NEGATIVE_TTL,
    POSITIVE_TTL,
    PlanespottersClient,
)


def _client(tmp_path: Path, enabled: bool = True) -> PlanespottersClient:
    return PlanespottersClient(
        cache_path=tmp_path / "photos.json.gz",
        enabled=enabled,
    )


def test_disabled_flag_is_respected(tmp_path: Path):
    c = _client(tmp_path, enabled=False)
    assert c.enabled is False
    # A disabled client never touches the cache or upstream.
    assert asyncio.run(c.lookup("G-EZAN")) is None


def test_lookup_returns_none_for_empty_registration(tmp_path: Path):
    c = _client(tmp_path)
    assert asyncio.run(c.lookup("")) is None
    assert asyncio.run(c.lookup("   ")) is None


def test_cache_hit_skips_upstream(tmp_path: Path):
    c = _client(tmp_path)
    payload = {
        "thumbnail": "https://img.test/t.jpg",
        "large": "https://img.test/l.jpg",
        "link": "https://planespotters.net/photo/abc",
        "photographer": "John Doe",
    }
    fake = AsyncMock(return_value=payload)
    with patch.object(c, "_fetch", fake):
        first = asyncio.run(c.lookup("G-EZAN"))
        second = asyncio.run(c.lookup("G-EZAN"))
    assert first == payload
    assert second == payload
    assert fake.await_count == 1


def test_registration_key_is_normalised(tmp_path: Path):
    c = _client(tmp_path)
    fake = AsyncMock(
        return_value={"thumbnail": "x", "large": "x", "link": None, "photographer": None}
    )
    with patch.object(c, "_fetch", fake):
        asyncio.run(c.lookup("  g-ezan "))
        asyncio.run(c.lookup("G-EZAN"))
    assert fake.await_count == 1
    assert "G-EZAN" in c._cache


def test_negative_response_caches_shorter(tmp_path: Path):
    c = _client(tmp_path)
    with patch.object(c, "_fetch", AsyncMock(return_value=None)):
        asyncio.run(c.lookup("N-UNK"))
    entry = c._cache["N-UNK"]
    assert entry["expires_at"] - time.time() <= NEGATIVE_TTL
    assert entry["expires_at"] - time.time() < POSITIVE_TTL


def test_429_sets_cooldown(tmp_path: Path):
    c = _client(tmp_path)
    req = httpx.Request("GET", "https://example/test")
    resp = httpx.Response(429, headers={"retry-after": "20"}, request=req)
    err = httpx.HTTPStatusError("rate limited", request=req, response=resp)
    with patch.object(c, "_fetch", AsyncMock(side_effect=err)):
        asyncio.run(c.lookup("G-EZAN"))
    delta = c._cooldown_until - time.time()
    assert 15 < delta < 25


def test_upstream_failure_falls_back_to_stale_cache(tmp_path: Path):
    c = _client(tmp_path)
    c._cache["STALE"] = {
        "data": {"thumbnail": "old", "large": "old", "link": None, "photographer": None},
        "expires_at": 0,  # stale
    }
    with patch.object(c, "_fetch", AsyncMock(side_effect=RuntimeError("boom"))):
        result = asyncio.run(c.lookup("STALE"))
    assert result == {"thumbnail": "old", "large": "old", "link": None, "photographer": None}


def test_cache_persists_and_loads(tmp_path: Path):
    path = tmp_path / "photos.json.gz"
    payload = {"thumbnail": "x", "large": "x", "link": None, "photographer": None}
    c1 = PlanespottersClient(cache_path=path)
    with patch.object(c1, "_fetch", AsyncMock(return_value=payload)):
        asyncio.run(c1.lookup("N123"))
    c2 = PlanespottersClient(cache_path=path)
    assert "N123" in c2._cache
    assert c2._cache["N123"]["data"] == payload


def test_old_schema_cache_is_ignored(tmp_path: Path):
    path = tmp_path / "photos.json.gz"
    with gzip.open(path, "wt", encoding="utf-8") as fh:
        json.dump(
            {
                "version": CACHE_SCHEMA_VERSION + 99,
                "cache": {
                    "OLD": {"data": {"thumbnail": "x"}, "expires_at": time.time() + 3600},
                },
            },
            fh,
        )
    c = PlanespottersClient(cache_path=path)
    assert c._cache == {}


def test_expired_entries_are_dropped_on_reload(tmp_path: Path):
    path = tmp_path / "photos.json.gz"
    with gzip.open(path, "wt", encoding="utf-8") as fh:
        json.dump(
            {
                "version": CACHE_SCHEMA_VERSION,
                "cache": {
                    "STALE": {"data": {"thumbnail": "x"}, "expires_at": 0},
                    "FRESH": {"data": {"thumbnail": "y"}, "expires_at": time.time() + 3600},
                },
            },
            fh,
        )
    c = PlanespottersClient(cache_path=path)
    assert "STALE" not in c._cache
    assert "FRESH" in c._cache


def test_aclose_releases_shared_client(tmp_path: Path):
    c = _client(tmp_path)
    # Force the HTTP client into existence without actually making requests.
    _ = c._client()
    assert c._http is not None
    asyncio.run(c.aclose())
    assert c._http is None
