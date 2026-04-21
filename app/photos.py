"""Aircraft photo lookups against planespotters.net's public API.

Planespotters hosts a community-maintained photo library with a free
public endpoint at `/pub/photos/reg/<registration>`. The photos are
generally better-framed and higher-resolution than adsbdb's upstream
(airport-data.com), and planespotters' terms permit hotlinking as
long as the photographer credit and a link back to the photo page
are shown — both of which we surface in the detail panel.

We use planespotters as the preferred photo source; callers can fall
back to adsbdb's photo URL when planespotters has nothing for a given
tail (small operators, brand-new registrations, experimental /
military aircraft).

Cached on disk for 30 days (positive) / 24h (negative), with a polite
1.2 s spacing between upstream calls and a 429 cooldown — same pattern
as the adsbdb client.
"""

import asyncio
import gzip
import json
import logging
import time
from pathlib import Path
from typing import Any

import httpx

log = logging.getLogger("beast.photos")


PLANESPOTTERS_URL = "https://api.planespotters.net/pub/photos/reg/{reg}"

POSITIVE_TTL = 30 * 86400  # 30d — photos rarely change
NEGATIVE_TTL = 24 * 3600  # 24h — retry tails planespotters doesn't have
CACHE_MAX_SIZE = 10_000
MIN_REQUEST_INTERVAL = 1.2
DEFAULT_429_COOLDOWN = 60.0
CACHE_SCHEMA_VERSION = 1


def _parse_retry_after(resp: "httpx.Response") -> float:
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


class PlanespottersClient:
    """Registration → photo URL + photographer credit, with caching."""

    def __init__(self, cache_path: Path | None = None, enabled: bool = True) -> None:
        self._enabled = enabled
        self.cache_path = cache_path
        self._cache: dict[str, dict[str, Any]] = {}
        self._request_lock = asyncio.Lock()
        self._in_flight: set[str] = set()
        self._last_request_at: float = 0.0
        self._cooldown_until: float = 0.0
        # Lazily-initialised shared HTTP client so repeat lookups reuse the
        # keep-alive connection instead of re-handshaking per call.
        self._http: httpx.AsyncClient | None = None
        self._load_cache()

    def _client(self) -> httpx.AsyncClient:
        if self._http is None:
            self._http = httpx.AsyncClient(timeout=15)
        return self._http

    async def aclose(self) -> None:
        if self._http is not None:
            await self._http.aclose()
            self._http = None

    @property
    def enabled(self) -> bool:
        return self._enabled

    @staticmethod
    def _key(registration: str | None) -> str | None:
        if not registration:
            return None
        k = registration.strip().upper()
        return k or None

    def lookup_cached(self, registration: str) -> dict[str, Any] | None:
        key = self._key(registration)
        if key is None:
            return None
        entry = self._cache.get(key)
        if not entry:
            return None
        if time.time() >= entry["expires_at"]:
            return None
        return entry["data"]

    async def lookup(self, registration: str) -> dict[str, Any] | None:
        """Return {thumbnail, large, link, photographer} for this tail, or
        None if planespotters has no photos for it. Never raises — on
        upstream error, falls back to a stale cache entry if any.
        """
        if not self._enabled:
            return None
        key = self._key(registration)
        if key is None:
            return None
        now = time.time()
        cached = self._cache.get(key)
        if cached and now < cached["expires_at"]:
            return cached["data"]
        if now < self._cooldown_until:
            return cached["data"] if cached else None
        if key in self._in_flight:
            return cached["data"] if cached else None

        self._in_flight.add(key)
        try:
            async with self._request_lock:
                cached = self._cache.get(key)
                now = time.time()
                if cached and now < cached["expires_at"]:
                    return cached["data"]
                if now < self._cooldown_until:
                    return cached["data"] if cached else None
                wait = (self._last_request_at + MIN_REQUEST_INTERVAL) - now
                if wait > 0:
                    await asyncio.sleep(wait)
                try:
                    data = await self._fetch(key)
                except httpx.HTTPStatusError as e:
                    if e.response.status_code == 429:
                        retry_after = _parse_retry_after(e.response)
                        self._cooldown_until = time.time() + retry_after
                        log.warning(
                            "planespotters 429 for %s — cooling down for %.0fs",
                            key,
                            retry_after,
                        )
                    else:
                        log.warning(
                            "planespotters HTTP %s for %s: %s",
                            e.response.status_code,
                            key,
                            e,
                        )
                    return cached["data"] if cached else None
                except Exception as e:
                    log.warning("planespotters lookup failed for %s: %s", key, e)
                    return cached["data"] if cached else None
                finally:
                    self._last_request_at = time.time()
            ttl = POSITIVE_TTL if data else NEGATIVE_TTL
            self._cache[key] = {"data": data, "expires_at": time.time() + ttl}
            self._prune()
            self._persist_cache()
            return data
        finally:
            self._in_flight.discard(key)

    async def _fetch(self, registration: str) -> dict[str, Any] | None:
        url = PLANESPOTTERS_URL.format(reg=registration)
        r = await self._client().get(url, headers={"accept": "application/json"})
        # 404 isn't documented for this endpoint — a valid request with
        # no matching photos returns 200 with photos=[]. Handle both.
        if r.status_code == 404:
            return None
        r.raise_for_status()
        body = r.json() or {}
        photos = body.get("photos") or []
        if not photos:
            return None
        # Take the first (newest) photo. Planespotters returns them
        # sorted by id desc already.
        p = photos[0]
        thumb = (p.get("thumbnail") or {}).get("src")
        large = (p.get("thumbnail_large") or {}).get("src") or thumb
        link = p.get("link")
        photographer = p.get("photographer")
        if not thumb:
            return None
        return {
            "thumbnail": thumb,
            "large": large,
            "link": link,
            "photographer": photographer,
        }

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
            log.warning("photos cache unreadable at %s: %s", self.cache_path, e)
            return
        if data.get("version") != CACHE_SCHEMA_VERSION:
            log.info(
                "photos cache schema %r != %d — starting fresh",
                data.get("version"),
                CACHE_SCHEMA_VERSION,
            )
            return
        now = time.time()
        for k, v in (data.get("cache") or {}).items():
            if isinstance(v, dict) and v.get("expires_at", 0) > now:
                self._cache[k] = v
        log.info("loaded %d planespotters photo cache entries", len(self._cache))

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
            log.warning("couldn't persist photos cache: %s", e)
