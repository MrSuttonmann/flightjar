"""Airport weather (METAR) lookups against aviationweather.gov.

aviationweather.gov publishes NOAA's real-time METAR / TAF data under a
free, no-auth public API. We batch airport codes into a single request
(one call per snapshot tick that needs fresh data, not one per airport),
cache hits on disk for 10 minutes (METARs cycle roughly hourly; SPECIs
mid-hour; 10 min keeps the panel current without hammering the
endpoint), negative responses for 5 minutes.

Feature-gated behind `METAR_WEATHER`, mirroring the `FLIGHT_ROUTES`
pattern — set `METAR_WEATHER=0` to disable all outbound lookups and
suppress the weather block from the UI.
"""

from __future__ import annotations

import asyncio
import gzip
import json
import logging
import time
from collections.abc import Iterable
from pathlib import Path
from typing import Any

import httpx

log = logging.getLogger("beast.metar")


def _parse_retry_after(resp: httpx.Response) -> float:
    """Read Retry-After (seconds or HTTP-date); fall back to the default."""
    raw = resp.headers.get("retry-after")
    if not raw:
        return DEFAULT_429_COOLDOWN
    try:
        return float(raw)
    except ValueError:
        from email.utils import parsedate_to_datetime

        try:
            return max(0.0, (parsedate_to_datetime(raw).timestamp() - time.time()))
        except Exception:
            return DEFAULT_429_COOLDOWN


METAR_URL = "https://aviationweather.gov/api/data/metar"

POSITIVE_TTL = 10 * 60  # 10 min — METARs are usually hourly with SPECIs
NEGATIVE_TTL = 5 * 60  # 5 min — retry unknowns periodically
CACHE_MAX_SIZE = 2_000
MIN_REQUEST_INTERVAL = 2.0  # seconds between upstream calls (polite default)
DEFAULT_429_COOLDOWN = 120.0
CACHE_SCHEMA_VERSION = 1

# Cloud-cover rank for picking the "headline" layer when a METAR reports
# several. Higher = more cloudy. Pilots read ceiling off the first BKN/
# OVC layer; for our thumbnail summary we surface the max.
_COVER_RANK = {"SKC": 0, "CLR": 0, "FEW": 1, "SCT": 2, "BKN": 3, "OVC": 4}


def _headline_cover(clouds: list[dict[str, Any]] | None) -> str | None:
    """Return the most-significant cloud-cover code across all reported
    layers, or None when the METAR doesn't carry cloud data."""
    if not clouds:
        return None
    best: str | None = None
    best_rank = -1
    for layer in clouds:
        code = (layer.get("cover") or "").upper()
        rank = _COVER_RANK.get(code, -1)
        if rank > best_rank:
            best_rank = rank
            best = code
    return best


def _distill(obs: dict[str, Any]) -> dict[str, Any] | None:
    """Reduce one aviationweather metar entry to the handful of fields the
    UI actually renders. Returns None when the entry is unusably empty."""
    if not isinstance(obs, dict):
        return None
    raw = obs.get("rawOb") or obs.get("rawOB") or None
    if not raw:
        return None
    return {
        "raw": raw,
        "obs_time": obs.get("obsTime"),
        "wind_dir": obs.get("wdir"),
        "wind_kt": obs.get("wspd"),
        "gust_kt": obs.get("wgst"),
        # Visibility is a string on non-US stations (metres) and often
        # "10+" on US stations. Pass it through verbatim — the UI
        # displays it alongside the unit hint from the raw METAR.
        "visibility": obs.get("visib"),
        "temp_c": obs.get("temp"),
        "dewpoint_c": obs.get("dewp"),
        "altimeter_hpa": obs.get("altim"),
        "cover": _headline_cover(obs.get("clouds")),
    }


class MetarClient:
    """Batched aviationweather.gov METAR lookups with on-disk caching."""

    def __init__(self, cache_path: Path | None = None, enabled: bool = True) -> None:
        self._enabled = enabled
        self.cache_path = cache_path
        self._cache: dict[str, dict[str, Any]] = {}
        self._request_lock = asyncio.Lock()
        self._in_flight: set[str] = set()
        self._last_request_at: float = 0.0
        self._cooldown_until: float = 0.0
        self._http: httpx.AsyncClient | None = None
        self._load_cache()

    @property
    def enabled(self) -> bool:
        return self._enabled

    def _client(self) -> httpx.AsyncClient:
        if self._http is None:
            self._http = httpx.AsyncClient(timeout=20)
        return self._http

    async def aclose(self) -> None:
        if self._http is not None:
            await self._http.aclose()
            self._http = None

    @staticmethod
    def _key(icao: str | None) -> str | None:
        if not icao:
            return None
        k = icao.strip().upper()
        if len(k) < 3 or len(k) > 4 or not k.isalnum():
            return None
        return k

    def lookup_cached(self, icao: str) -> tuple[bool, dict[str, Any] | None]:
        """Return (known, data) for this airport — see AdsbdbClient's
        lookup_cached_* for the semantics of the `known` flag."""
        key = self._key(icao)
        if key is None:
            return False, None
        entry = self._cache.get(key)
        if not entry or time.time() >= entry["expires_at"]:
            return False, None
        return True, entry["data"]

    async def lookup(self, icao: str) -> dict[str, Any] | None:
        """Fetch (or cache-hit) a single airport's METAR. Never raises —
        falls back to a stale cache entry on upstream errors."""
        result = await self.lookup_many([icao])
        return result.get(self._key(icao) or "")

    async def lookup_many(self, codes: Iterable[str]) -> dict[str, dict[str, Any] | None]:
        """Fetch (or cache-hit) METARs for a batch of airport codes in a
        single upstream call. Returns a map from normalised ICAO code to
        either the METAR dict or None for a cached-negative / miss."""
        if not self._enabled:
            return {}
        out: dict[str, dict[str, Any] | None] = {}
        fresh_keys: list[str] = []
        now = time.time()
        for raw in codes:
            key = self._key(raw)
            if key is None or key in out:
                continue
            entry = self._cache.get(key)
            if entry and now < entry["expires_at"]:
                out[key] = entry["data"]
            else:
                fresh_keys.append(key)
        if not fresh_keys:
            return out
        if now < self._cooldown_until:
            for key in fresh_keys:
                cached = self._cache.get(key)
                out[key] = cached["data"] if cached else None
            return out
        # Dedupe concurrent batch requests on exactly this key set.
        dedup = ",".join(sorted(fresh_keys))
        if dedup in self._in_flight:
            for key in fresh_keys:
                cached = self._cache.get(key)
                out[key] = cached["data"] if cached else None
            return out

        self._in_flight.add(dedup)
        try:
            async with self._request_lock:
                now = time.time()
                if now < self._cooldown_until:
                    for key in fresh_keys:
                        cached = self._cache.get(key)
                        out[key] = cached["data"] if cached else None
                    return out
                wait = (self._last_request_at + MIN_REQUEST_INTERVAL) - now
                if wait > 0:
                    await asyncio.sleep(wait)
                try:
                    payload = await self._fetch(fresh_keys)
                except httpx.HTTPStatusError as e:
                    if e.response.status_code == 429:
                        retry_after = _parse_retry_after(e.response)
                        self._cooldown_until = time.time() + retry_after
                        log.warning(
                            "aviationweather 429 — cooling down for %.0fs",
                            retry_after,
                        )
                    else:
                        log.warning(
                            "aviationweather HTTP %s: %s",
                            e.response.status_code,
                            e,
                        )
                    for key in fresh_keys:
                        cached = self._cache.get(key)
                        out[key] = cached["data"] if cached else None
                    return out
                except Exception as e:
                    log.warning("aviationweather fetch failed: %s", e)
                    for key in fresh_keys:
                        cached = self._cache.get(key)
                        out[key] = cached["data"] if cached else None
                    return out
                finally:
                    self._last_request_at = time.time()
            # Merge the batch response into the cache — any key we asked
            # about that the API didn't return gets a cached-negative.
            now = time.time()
            for key in fresh_keys:
                data = payload.get(key)
                ttl = POSITIVE_TTL if data else NEGATIVE_TTL
                self._cache[key] = {"data": data, "expires_at": now + ttl}
                out[key] = data
            self._prune()
            self._persist_cache()
            return out
        finally:
            self._in_flight.discard(dedup)

    async def _fetch(self, keys: list[str]) -> dict[str, dict[str, Any]]:
        """One upstream call covering every key in the batch.

        Returns a dict keyed by normalised ICAO code with only the
        distilled fields we surface. Airports the API doesn't know are
        absent from the returned dict; callers map that to a
        cached-negative entry.
        """
        r = await self._client().get(
            METAR_URL,
            params={
                "ids": ",".join(keys),
                "format": "json",
                "taf": "false",
                "hours": "1",
            },
            headers={"accept": "application/json"},
        )
        if r.status_code == 404:
            return {}
        r.raise_for_status()
        body = r.json() or []
        if not isinstance(body, list):
            return {}
        out: dict[str, dict[str, Any]] = {}
        for entry in body:
            if not isinstance(entry, dict):
                continue
            ident = (entry.get("icaoId") or entry.get("icao") or "").upper()
            if not ident:
                continue
            distilled = _distill(entry)
            if distilled:
                out[ident] = distilled
        return out

    # -------- cache persistence --------

    def _prune(self) -> None:
        if len(self._cache) <= CACHE_MAX_SIZE:
            return
        keep = sorted(self._cache.items(), key=lambda kv: kv[1]["expires_at"], reverse=True)[
            :CACHE_MAX_SIZE
        ]
        self._cache = dict(keep)

    def _load_cache(self) -> None:
        if self.cache_path is None or not self.cache_path.exists():
            return
        try:
            with gzip.open(self.cache_path, "rt", encoding="utf-8") as fh:
                data = json.load(fh)
        except Exception as e:
            log.warning("metar cache unreadable at %s: %s", self.cache_path, e)
            return
        if data.get("version") != CACHE_SCHEMA_VERSION:
            log.info(
                "metar cache schema %r != %d — starting fresh",
                data.get("version"),
                CACHE_SCHEMA_VERSION,
            )
            return
        now = time.time()
        for k, v in (data.get("cache") or {}).items():
            if isinstance(v, dict) and v.get("expires_at", 0) > now:
                self._cache[k] = v
        log.info("loaded %d metar cache entries", len(self._cache))

    def _persist_cache(self) -> None:
        if self.cache_path is None:
            return
        try:
            self.cache_path.parent.mkdir(parents=True, exist_ok=True)
            tmp = self.cache_path.with_suffix(self.cache_path.suffix + ".tmp")
            with gzip.open(tmp, "wt", encoding="utf-8") as fh:
                json.dump(
                    {"version": CACHE_SCHEMA_VERSION, "cache": self._cache},
                    fh,
                    separators=(",", ":"),
                )
            tmp.replace(self.cache_path)
        except Exception as e:
            log.warning("couldn't persist metar cache: %s", e)
