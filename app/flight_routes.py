"""adsbdb.com lookups for flight routes and aircraft records.

adsbdb is a free, no-auth community API on top of crowd-sourced data.
Two endpoints matter to us:

- `/v0/callsign/<callsign>` — origin + destination airports for the
  flight currently (or most recently) operating under that callsign.
- `/v0/aircraft/<mode_s_hex>` — per-tail details (registration, type,
  operator, and a photo URL served by airport-data.com).

Lookups are lazy: a callsite asks for a key, we check the cache first,
fall back to the upstream API on miss or stale, and memoise the answer
with a TTL. Route hits cache 12h (flight plans are stable over a flight
and a little slack); aircraft hits cache 30 days (registrations and
photos change very rarely). Negative answers cache shorter so a
newly-seen callsign or tail gets retried periodically.

Both kinds share one throttle and one 429 cooldown — we're a polite
single client against a volunteer service.
"""

import asyncio
import gzip
import json
import logging
import time
from collections.abc import Awaitable, Callable
from pathlib import Path
from typing import Any

import httpx

log = logging.getLogger("beast.flight_routes")


def _parse_retry_after(resp: "httpx.Response") -> float:
    """Read the Retry-After header (seconds or HTTP-date); fall back to default."""
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


ADSBDB_CALLSIGN_URL = "https://api.adsbdb.com/v0/callsign/{key}"
ADSBDB_AIRCRAFT_URL = "https://api.adsbdb.com/v0/aircraft/{key}"

ROUTE_POSITIVE_TTL = 12 * 3600  # 12h — flight plans outlive a single flight
ROUTE_NEGATIVE_TTL = 1 * 3600  # 1h — retry "unknown callsign" periodically
AIRCRAFT_POSITIVE_TTL = 30 * 86400  # 30d — tails barely change
AIRCRAFT_NEGATIVE_TTL = 24 * 3600  # 24h — retry tails adsbdb doesn't know

CACHE_MAX_SIZE = 10_000  # per cache; bound memory on busy receivers
MIN_REQUEST_INTERVAL = 1.2  # seconds between consecutive upstream calls
DEFAULT_429_COOLDOWN = 60.0  # if adsbdb doesn't set Retry-After, back off this long
CACHE_SCHEMA_VERSION = 3  # bump on any key/schema change to invalidate on disk


class AdsbdbClient:
    """Route + aircraft lookups against adsbdb.com, with on-disk caching."""

    def __init__(self, cache_path: Path | None = None, enabled: bool = True) -> None:
        self._enabled = enabled
        self.cache_path = cache_path
        self._routes: dict[str, dict[str, Any]] = {}
        self._aircraft: dict[str, dict[str, Any]] = {}
        self._request_lock = asyncio.Lock()
        self._in_flight: set[str] = set()
        # Paced throttle shared across both endpoint kinds: bursty requests
        # are what can trip a community service's rate limiter. If adsbdb
        # 429s, back off until this wall-clock time before next attempt.
        self._last_request_at: float = 0.0
        self._cooldown_until: float = 0.0
        # Lazily-initialised shared HTTP client so repeat lookups reuse the
        # keep-alive connection instead of doing a TCP+TLS handshake per call.
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

    # -------- key normalisation --------

    @staticmethod
    def _route_key(callsign: str | None) -> str | None:
        if not callsign:
            return None
        k = callsign.strip().upper()
        return k or None

    @staticmethod
    def _aircraft_key(icao: str | None) -> str | None:
        if not icao:
            return None
        k = icao.strip().lower()
        if len(k) > 6 or not all(c in "0123456789abcdef" for c in k):
            return None
        return k or None

    # -------- public API: routes (by callsign) --------

    def lookup_cached_route(self, callsign: str) -> tuple[bool, dict[str, Any] | None]:
        """Return (known, data) for a cached route without touching the network.

        `known=True` means we have a fresh answer (which may be `None` for a
        cached-negative) so the caller should *not* fire a background lookup;
        `known=False` means the cache has nothing and a background fill is
        warranted.
        """
        key = self._route_key(callsign)
        if key is None:
            return False, None
        return self._cached(self._routes, key)

    async def lookup_route(self, callsign: str) -> dict[str, Any] | None:
        """Return {origin, destination, callsign} for this callsign, or None."""
        key = self._route_key(callsign)
        if key is None:
            return None
        return await self._lookup(
            self._routes,
            key,
            self._fetch_route,
            ROUTE_POSITIVE_TTL,
            ROUTE_NEGATIVE_TTL,
        )

    # -------- public API: aircraft (by mode-s hex) --------

    def lookup_cached_aircraft(self, icao: str) -> tuple[bool, dict[str, Any] | None]:
        """Return (known, data) for a cached aircraft record. See
        `lookup_cached_route` for the semantics of the `known` flag."""
        key = self._aircraft_key(icao)
        if key is None:
            return False, None
        return self._cached(self._aircraft, key)

    async def lookup_aircraft(self, icao: str) -> dict[str, Any] | None:
        """Return {registration, type, manufacturer, operator, photo_url,
        photo_thumbnail} for this ICAO24 hex, or None."""
        key = self._aircraft_key(icao)
        if key is None:
            return None
        return await self._lookup(
            self._aircraft,
            key,
            self._fetch_aircraft,
            AIRCRAFT_POSITIVE_TTL,
            AIRCRAFT_NEGATIVE_TTL,
        )

    # -------- shared cache + throttle machinery --------

    @staticmethod
    def _cached(bucket: dict[str, dict[str, Any]], key: str) -> tuple[bool, dict[str, Any] | None]:
        """Return (known, data) for a cache lookup.

        `known=False` covers both "never seen" and "entry expired" — in both
        cases the caller should refresh. `known=True, data=None` is a fresh
        cached-negative result.
        """
        entry = bucket.get(key)
        if not entry or time.time() >= entry["expires_at"]:
            return False, None
        return True, entry["data"]

    async def _lookup(
        self,
        bucket: dict[str, dict[str, Any]],
        key: str,
        fetcher: Callable[[str], Awaitable[dict[str, Any] | None]],
        positive_ttl: int,
        negative_ttl: int,
    ) -> dict[str, Any] | None:
        """Cache-aware lookup against one endpoint. Never raises — falls
        back to a stale cache entry on upstream errors.
        """
        if not self._enabled:
            return None
        now = time.time()
        cached = bucket.get(key)
        if cached and now < cached["expires_at"]:
            return cached["data"]
        if now < self._cooldown_until:
            return cached["data"] if cached else None
        # Dedupe concurrent callers on the same key, regardless of kind.
        dedup = f"{id(bucket)}:{key}"
        if dedup in self._in_flight:
            return cached["data"] if cached else None

        self._in_flight.add(dedup)
        try:
            async with self._request_lock:
                cached = bucket.get(key)
                now = time.time()
                if cached and now < cached["expires_at"]:
                    return cached["data"]
                if now < self._cooldown_until:
                    return cached["data"] if cached else None
                wait = (self._last_request_at + MIN_REQUEST_INTERVAL) - now
                if wait > 0:
                    await asyncio.sleep(wait)
                try:
                    data = await fetcher(key)
                except httpx.HTTPStatusError as e:
                    if e.response.status_code == 429:
                        retry_after = _parse_retry_after(e.response)
                        self._cooldown_until = time.time() + retry_after
                        log.warning(
                            "adsbdb 429 for %s — cooling down for %.0fs",
                            key,
                            retry_after,
                        )
                    else:
                        log.warning("adsbdb HTTP %s for %s: %s", e.response.status_code, key, e)
                    return cached["data"] if cached else None
                except Exception as e:
                    log.warning("adsbdb lookup failed for %s: %s", key, e)
                    return cached["data"] if cached else None
                finally:
                    self._last_request_at = time.time()
            ttl = positive_ttl if data else negative_ttl
            bucket[key] = {"data": data, "expires_at": time.time() + ttl}
            self._prune(bucket)
            self._persist_cache()
            return data
        finally:
            self._in_flight.discard(dedup)

    # -------- upstream calls --------

    async def _fetch_route(self, callsign: str) -> dict[str, Any] | None:
        """Ask adsbdb for the flight route for this callsign."""
        url = ADSBDB_CALLSIGN_URL.format(key=callsign)
        r = await self._client().get(url, headers={"accept": "application/json"})
        if r.status_code == 404:
            return None
        r.raise_for_status()
        body = r.json() or {}
        route = (body.get("response") or {}).get("flightroute")
        if not isinstance(route, dict):
            return None
        origin = (route.get("origin") or {}).get("icao_code")
        destination = (route.get("destination") or {}).get("icao_code")
        if not origin and not destination:
            return None
        return {
            "origin": origin or None,
            "destination": destination or None,
            "callsign": route.get("callsign") or callsign,
        }

    async def _fetch_aircraft(self, icao: str) -> dict[str, Any] | None:
        """Ask adsbdb for the aircraft record for this mode-s hex."""
        url = ADSBDB_AIRCRAFT_URL.format(key=icao)
        r = await self._client().get(url, headers={"accept": "application/json"})
        if r.status_code == 404:
            return None
        r.raise_for_status()
        body = r.json() or {}
        ac = (body.get("response") or {}).get("aircraft")
        if not isinstance(ac, dict):
            return None
        return {
            "registration": ac.get("registration") or None,
            "type": ac.get("type") or None,
            "icao_type": ac.get("icao_type") or None,
            "manufacturer": ac.get("manufacturer") or None,
            "operator": ac.get("registered_owner") or None,
            "operator_country": ac.get("registered_owner_country_name") or None,
            "operator_country_iso": ac.get("registered_owner_country_iso_name") or None,
            "photo_url": ac.get("url_photo") or None,
            "photo_thumbnail": ac.get("url_photo_thumbnail") or None,
        }

    # -------- cache persistence --------

    def _prune(self, bucket: dict[str, dict[str, Any]]) -> None:
        if len(bucket) <= CACHE_MAX_SIZE:
            return
        keep = sorted(bucket.items(), key=lambda kv: kv[1]["expires_at"], reverse=True)[
            :CACHE_MAX_SIZE
        ]
        bucket.clear()
        bucket.update(keep)

    def _load_cache(self) -> None:
        if self.cache_path is None or not self.cache_path.exists():
            return
        try:
            with gzip.open(self.cache_path, "rt", encoding="utf-8") as fh:
                data = json.load(fh)
        except Exception as e:
            log.warning("flight-route cache unreadable at %s: %s", self.cache_path, e)
            return
        # Older caches (v<3) used a single top-level "cache" dict and aren't
        # useful under the new two-bucket layout — drop them silently.
        if data.get("version") != CACHE_SCHEMA_VERSION:
            log.info(
                "flight-route cache schema %r != %d — starting fresh",
                data.get("version"),
                CACHE_SCHEMA_VERSION,
            )
            return
        now = time.time()
        for bucket_name, bucket in (("routes", self._routes), ("aircraft", self._aircraft)):
            for k, v in (data.get(bucket_name) or {}).items():
                if isinstance(v, dict) and v.get("expires_at", 0) > now:
                    bucket[k] = v
        log.info(
            "loaded %d route + %d aircraft cache entries",
            len(self._routes),
            len(self._aircraft),
        )

    def _persist_cache(self) -> None:
        if self.cache_path is None:
            return
        try:
            self.cache_path.parent.mkdir(parents=True, exist_ok=True)
            tmp = self.cache_path.with_suffix(self.cache_path.suffix + ".tmp")
            with gzip.open(tmp, "wt", encoding="utf-8") as fh:
                json.dump(
                    {
                        "version": CACHE_SCHEMA_VERSION,
                        "routes": self._routes,
                        "aircraft": self._aircraft,
                    },
                    fh,
                    separators=(",", ":"),
                )
            tmp.replace(self.cache_path)
        except Exception as e:
            log.warning("couldn't persist flight-route cache: %s", e)
