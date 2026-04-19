"""adsbdb.com origin → destination lookup keyed by callsign.

adsbdb is a free, no-auth community API built on top of crowd-sourced
flight-route data. We ask it for the origin and destination airport ICAO
codes for a given callsign (e.g. `BAW123`) and cache the answer for a
while so popups stay snappy and we don't hammer a volunteer service.

Lookups are lazy: a callsite asks for a callsign, we check the cache
first, fall back to the upstream API on miss or stale, and memoise the
answer with a TTL. Positive hits are cached for 12h (flight plans are
stable for the duration of a flight + a little slack); negative answers
("unknown callsign" — often registrations for GA / military) for 1h so
we re-ask eventually without re-asking on every popup open.

This replaces an earlier OpenSky integration whose credit-limited API
and aircraft-hex key proved too restrictive for the popup use case.
"""

import asyncio
import gzip
import json
import logging
import time
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


ADSBDB_CALLSIGN_URL = "https://api.adsbdb.com/v0/callsign/{callsign}"

CACHE_POSITIVE_TTL = 12 * 3600  # 12h — flight plans outlive a single flight
CACHE_NEGATIVE_TTL = 1 * 3600  # 1h — retry "unknown callsign" periodically
CACHE_MAX_SIZE = 10_000  # bound memory on busy receivers
MIN_REQUEST_INTERVAL = 1.2  # seconds between consecutive upstream calls
DEFAULT_429_COOLDOWN = 60.0  # if adsbdb doesn't set Retry-After, back off this long
CACHE_SCHEMA_VERSION = 2  # bump to invalidate on-disk cache after a key/schema change


class AdsbdbClient:
    """Callsign → route lookup against adsbdb.com, with on-disk caching."""

    def __init__(self, cache_path: Path | None = None, enabled: bool = True) -> None:
        self._enabled = enabled
        self.cache_path = cache_path
        self._cache: dict[str, dict[str, Any]] = {}
        self._request_lock = asyncio.Lock()
        self._in_flight: set[str] = set()
        # Paced throttle: requests are serialised and spaced by at least
        # MIN_REQUEST_INTERVAL. If adsbdb 429s, we back off until this
        # wall-clock time before allowing the next call.
        self._last_request_at: float = 0.0
        self._cooldown_until: float = 0.0
        self._load_cache()

    @property
    def enabled(self) -> bool:
        return self._enabled

    # -------- key normalisation --------

    @staticmethod
    def _key(callsign: str | None) -> str | None:
        if not callsign:
            return None
        k = callsign.strip().upper()
        return k or None

    # -------- public API --------

    def lookup_cached(self, callsign: str) -> dict[str, Any] | None:
        """Return cached data for this callsign without ever hitting the network.

        Returns the cached payload (possibly `None`, for negative hits) when
        still fresh, else the literal `None` when nothing's known. Callers
        can tell the difference by checking the cache directly if needed;
        for snapshot enrichment both "fresh-None" and "unknown" render the
        same.
        """
        key = self._key(callsign)
        if key is None:
            return None
        cached = self._cache.get(key)
        if not cached:
            return None
        if time.time() >= cached["expires_at"]:
            return None
        return cached["data"]

    async def lookup(self, callsign: str) -> dict[str, Any] | None:
        """Return {origin, destination, callsign} for this callsign, or None.

        Uses the cache when fresh. Never raises — on upstream error, falls
        back to a stale cache entry if we have one, otherwise None.
        Concurrent calls for the same callsign are deduplicated; the second
        caller returns whatever's already cached without firing a new
        request.
        """
        if not self._enabled:
            return None
        key = self._key(callsign)
        if key is None:
            return None
        now = time.time()
        cached = self._cache.get(key)
        if cached and now < cached["expires_at"]:
            return cached["data"]
        # If adsbdb 429'd us recently, don't even try.
        if now < self._cooldown_until:
            return cached["data"] if cached else None
        if key in self._in_flight:
            return cached["data"] if cached else None

        self._in_flight.add(key)
        try:
            # Serialise requests and space them; bursty requests are what
            # can trip a community service's rate limiter.
            async with self._request_lock:
                # Re-check cache inside the lock — a sibling coroutine may
                # have filled it while we were waiting.
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
            ttl = CACHE_POSITIVE_TTL if data else CACHE_NEGATIVE_TTL
            self._cache[key] = {"data": data, "expires_at": time.time() + ttl}
            self._prune_cache()
            self._persist_cache()
            return data
        finally:
            self._in_flight.discard(key)

    # -------- upstream call --------

    async def _fetch(self, callsign: str) -> dict[str, Any] | None:
        """Ask adsbdb for the flight route for this callsign.

        adsbdb returns 404 with a `{"response": "unknown callsign"}` body
        when it's never seen the callsign (e.g. a registration rather than
        a flight number, or a brand-new callsign not yet in the dataset).
        We surface that as `None` so callers can cache it as a negative
        hit.
        """
        url = ADSBDB_CALLSIGN_URL.format(callsign=callsign)
        async with httpx.AsyncClient(timeout=15) as c:
            r = await c.get(url, headers={"accept": "application/json"})
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

    # -------- cache persistence --------

    def _prune_cache(self) -> None:
        if len(self._cache) <= CACHE_MAX_SIZE:
            return
        # Drop the oldest-expiring entries first.
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
            log.warning("flight-route cache unreadable at %s: %s", self.cache_path, e)
            return
        # Old OpenSky-era caches used ICAO24 keys and no version marker.
        # Drop them silently — they'd just sit unused under the new scheme.
        if data.get("version") != CACHE_SCHEMA_VERSION:
            log.info(
                "flight-route cache schema %r != %d — starting fresh",
                data.get("version"),
                CACHE_SCHEMA_VERSION,
            )
            return
        now = time.time()
        for k, v in (data.get("cache") or {}).items():
            if isinstance(v, dict) and v.get("expires_at", 0) > now:
                self._cache[k] = v
        log.info("loaded %d flight-route cache entries", len(self._cache))

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
            log.warning("couldn't persist flight-route cache: %s", e)
