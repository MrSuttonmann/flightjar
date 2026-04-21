"""Tests for the aviationweather.gov METAR client."""

import asyncio
import gzip
import json
import time
from pathlib import Path
from unittest.mock import AsyncMock, patch

import httpx

from app.metar import (
    CACHE_SCHEMA_VERSION,
    NEGATIVE_TTL,
    POSITIVE_TTL,
    MetarClient,
    _distill,
    _headline_cover,
)


def _client(tmp_path: Path, enabled: bool = True) -> MetarClient:
    return MetarClient(cache_path=tmp_path / "metar.json.gz", enabled=enabled)


# -------- helpers --------


def test_headline_cover_picks_the_most_significant_layer():
    layers = [{"cover": "FEW", "base": 2000}, {"cover": "BKN", "base": 5000}]
    assert _headline_cover(layers) == "BKN"


def test_headline_cover_handles_missing_or_unknown_entries():
    assert _headline_cover(None) is None
    assert _headline_cover([]) is None
    assert _headline_cover([{"cover": "WHAT"}]) is None


def test_distill_drops_entries_without_raw_metar():
    assert _distill({}) is None
    assert _distill({"icaoId": "EGLL"}) is None
    assert _distill("not a dict") is None  # type: ignore[arg-type]


def test_distill_projects_relevant_fields():
    raw = {
        "icaoId": "EGLL",
        "rawOb": "EGLL 131950Z AUTO 25012KT 9999 BKN020 15/10 Q1015",
        "obsTime": 1730000000,
        "wdir": 250,
        "wspd": 12,
        "wgst": None,
        "visib": "10+",
        "temp": 15,
        "dewp": 10,
        "altim": 1015,
        "clouds": [{"cover": "BKN", "base": 2000}],
    }
    d = _distill(raw)
    assert d is not None
    assert d["raw"].startswith("EGLL")
    assert d["wind_dir"] == 250
    assert d["wind_kt"] == 12
    assert d["visibility"] == "10+"
    assert d["cover"] == "BKN"


# -------- client behaviour --------


def test_disabled_client_skips_everything(tmp_path: Path):
    c = _client(tmp_path, enabled=False)
    assert c.enabled is False
    # Disabled: lookup_many returns an empty dict, never calls upstream.
    with patch.object(c, "_fetch", AsyncMock()) as fake:
        result = asyncio.run(c.lookup_many(["EGLL", "KJFK"]))
    assert result == {}
    fake.assert_not_awaited()


def test_key_normalisation_rejects_bad_input(tmp_path: Path):
    c = _client(tmp_path)
    assert c._key("") is None
    assert c._key("xx") is None  # too short
    assert c._key("toolong") is None
    assert c._key("bad!") is None
    assert c._key("  egll ") == "EGLL"


def test_cache_hit_skips_upstream(tmp_path: Path):
    c = _client(tmp_path)
    payload = {"EGLL": {"raw": "EGLL ...", "cover": "BKN"}}
    fake = AsyncMock(return_value=payload)
    with patch.object(c, "_fetch", fake):
        first = asyncio.run(c.lookup_many(["EGLL"]))
        second = asyncio.run(c.lookup_many(["EGLL"]))
    assert first["EGLL"] == payload["EGLL"]
    assert second["EGLL"] == payload["EGLL"]
    assert fake.await_count == 1


def test_missing_airports_cache_as_negative(tmp_path: Path):
    """Codes we ask about that the API doesn't return get a cached-negative
    entry so we don't re-fetch them for NEGATIVE_TTL seconds."""
    c = _client(tmp_path)
    # API only returns EGLL; ZZZZ is absent.
    with patch.object(c, "_fetch", AsyncMock(return_value={"EGLL": {"raw": "..."}})):
        result = asyncio.run(c.lookup_many(["EGLL", "ZZZZ"]))
    assert result["EGLL"] is not None
    assert result["ZZZZ"] is None
    # Negative TTL is shorter than positive.
    zzzz = c._cache["ZZZZ"]
    assert zzzz["expires_at"] - time.time() <= NEGATIVE_TTL
    assert zzzz["expires_at"] - time.time() < POSITIVE_TTL


def test_429_sets_cooldown_and_suppresses_followups(tmp_path: Path):
    c = _client(tmp_path)
    req = httpx.Request("GET", "https://example/test")
    resp = httpx.Response(429, headers={"retry-after": "30"}, request=req)
    err = httpx.HTTPStatusError("rate limited", request=req, response=resp)
    with patch.object(c, "_fetch", AsyncMock(side_effect=err)) as fake:
        asyncio.run(c.lookup_many(["EGLL"]))  # first call 429s
        asyncio.run(c.lookup_many(["KJFK"]))  # should be suppressed
    assert fake.await_count == 1
    delta = c._cooldown_until - time.time()
    assert 25 < delta < 35


def test_upstream_failure_falls_back_to_stale_cache(tmp_path: Path):
    c = _client(tmp_path)
    c._cache["EGLL"] = {
        "data": {"raw": "old"},
        "expires_at": 0,  # stale
    }
    with patch.object(c, "_fetch", AsyncMock(side_effect=RuntimeError("boom"))):
        result = asyncio.run(c.lookup_many(["EGLL"]))
    assert result["EGLL"] == {"raw": "old"}


def test_lookup_cached_distinguishes_miss_from_negative(tmp_path: Path):
    c = _client(tmp_path)
    assert c.lookup_cached("EGLL") == (False, None)
    with patch.object(c, "_fetch", AsyncMock(return_value={})):
        asyncio.run(c.lookup_many(["EGLL"]))
    # Known cached-negative now.
    assert c.lookup_cached("EGLL") == (True, None)
    with patch.object(c, "_fetch", AsyncMock(return_value={"KJFK": {"raw": "k"}})):
        asyncio.run(c.lookup_many(["KJFK"]))
    assert c.lookup_cached("KJFK") == (True, {"raw": "k"})


def test_cache_persists_and_loads(tmp_path: Path):
    path = tmp_path / "metar.json.gz"
    payload = {"EGLL": {"raw": "EGLL ..."}}
    c1 = MetarClient(cache_path=path)
    with patch.object(c1, "_fetch", AsyncMock(return_value=payload)):
        asyncio.run(c1.lookup_many(["EGLL"]))
    c2 = MetarClient(cache_path=path)
    assert "EGLL" in c2._cache
    assert c2._cache["EGLL"]["data"] == payload["EGLL"]


def test_old_schema_cache_is_ignored(tmp_path: Path):
    path = tmp_path / "metar.json.gz"
    with gzip.open(path, "wt", encoding="utf-8") as fh:
        json.dump(
            {
                "version": CACHE_SCHEMA_VERSION + 99,
                "cache": {"OLD": {"data": {"raw": "x"}, "expires_at": time.time() + 3600}},
            },
            fh,
        )
    c = MetarClient(cache_path=path)
    assert c._cache == {}


def test_expired_entries_are_dropped_on_reload(tmp_path: Path):
    path = tmp_path / "metar.json.gz"
    with gzip.open(path, "wt", encoding="utf-8") as fh:
        json.dump(
            {
                "version": CACHE_SCHEMA_VERSION,
                "cache": {
                    "STALE": {"data": {"raw": "x"}, "expires_at": 0},
                    "FRESH": {"data": {"raw": "y"}, "expires_at": time.time() + 3600},
                },
            },
            fh,
        )
    c = MetarClient(cache_path=path)
    assert "STALE" not in c._cache
    assert "FRESH" in c._cache


def test_aclose_releases_shared_client(tmp_path: Path):
    c = _client(tmp_path)
    _ = c._client()
    assert c._http is not None
    asyncio.run(c.aclose())
    assert c._http is None


def test_lookup_single_delegates_to_batch(tmp_path: Path):
    c = _client(tmp_path)
    payload = {"EGLL": {"raw": "EGLL ...", "cover": "CLR"}}
    with patch.object(c, "_fetch", AsyncMock(return_value=payload)):
        result = asyncio.run(c.lookup("egll"))
    assert result == payload["EGLL"]
