"""OpenSky Network origin → destination lookup for an ICAO24.

Uses OAuth2 client credentials (OpenSky migrated off HTTP Basic in 2024 —
sign in at https://opensky-network.org/my-opensky and click 'Create API
Client' to get a client_id + client_secret pair). Contributor rate limits
(~8000 credits/day) apply when the credentials belong to a feeder account,
which is plenty for our on-demand usage.

Lookups are lazy: a callsite asks for a single ICAO24, we check the cache
first, fall back to the upstream API on miss or stale, and memoise the
answer with a TTL. Positive hits are cached for 12h (flight plans are
stable for the duration of a flight + a little slack); negative answers
("no recent flight found") for 1h so we don't re-ask the upstream API
every time a popup opens.

The module is feature-gated — if no credentials are configured, every
lookup returns None and the endpoint short-circuits. No upstream calls,
no background tokens, no file writes.
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

OPENSKY_TOKEN_URL = (
    "https://auth.opensky-network.org/auth/realms/opensky-network/protocol/openid-connect/token"
)
OPENSKY_FLIGHTS_URL = "https://opensky-network.org/api/flights/aircraft"

CACHE_POSITIVE_TTL = 12 * 3600  # 12h — flight plans outlive a single flight
CACHE_NEGATIVE_TTL = 1 * 3600  # 1h — retry "unknown" results periodically
CACHE_MAX_SIZE = 10_000  # bound memory on busy receivers
LOOKUP_WINDOW_HOURS = 12  # how far back to query OpenSky for flights
TOKEN_REFRESH_SKEW = 60  # refresh when <= this many seconds remain
MAX_CONCURRENT_LOOKUPS = 3  # throttle upstream fan-out for busy skies


class OpenSkyClient:
    """Lazy OAuth token + /flights/aircraft lookup with on-disk caching."""

    def __init__(
        self,
        client_id: str | None = None,
        client_secret: str | None = None,
        cache_path: Path | None = None,
    ) -> None:
        self.client_id = client_id
        self.client_secret = client_secret
        self.cache_path = cache_path
        self._token: str | None = None
        self._token_expires_at: float = 0.0
        self._cache: dict[str, dict[str, Any]] = {}
        self._token_lock = asyncio.Lock()
        self._in_flight: set[str] = set()
        self._semaphore = asyncio.Semaphore(MAX_CONCURRENT_LOOKUPS)
        self._load_cache()

    @property
    def enabled(self) -> bool:
        return bool(self.client_id and self.client_secret)

    # -------- public API --------

    def lookup_cached(self, icao24: str) -> dict[str, Any] | None:
        """Return cached data for this ICAO24 without ever hitting the network.

        Returns the cached payload (possibly `None`, for negative hits) when
        still fresh, else the literal `None` when nothing's known. Callers
        can tell the difference by checking the cache directly if needed;
        for snapshot enrichment both "fresh-None" and "unknown" render the
        same.
        """
        cached = self._cache.get((icao24 or "").lower())
        if not cached:
            return None
        if time.time() >= cached["expires_at"]:
            return None
        return cached["data"]

    async def lookup(self, icao24: str) -> dict[str, Any] | None:
        """Return {origin, destination, callsign} for this ICAO24, or None.

        Uses the cache when fresh. Never raises — on upstream error, falls
        back to a stale cache entry if we have one, otherwise None.
        Concurrent calls for the same ICAO are deduplicated; the second
        caller returns whatever's already cached without firing a new
        request.
        """
        if not self.enabled or not icao24:
            return None
        key = icao24.lower()
        now = time.time()
        cached = self._cache.get(key)
        if cached and now < cached["expires_at"]:
            return cached["data"]
        if key in self._in_flight:
            return cached["data"] if cached else None

        self._in_flight.add(key)
        try:
            async with self._semaphore:
                try:
                    data = await self._fetch(key)
                except Exception as e:
                    log.warning("opensky lookup failed for %s: %s", key, e)
                    return cached["data"] if cached else None
            ttl = CACHE_POSITIVE_TTL if data else CACHE_NEGATIVE_TTL
            self._cache[key] = {"data": data, "expires_at": time.time() + ttl}
            self._prune_cache()
            self._persist_cache()
            return data
        finally:
            self._in_flight.discard(key)

    # -------- token management --------

    async def _get_token(self) -> str:
        """Fetch or refresh the OAuth access token."""
        async with self._token_lock:
            now = time.time()
            if self._token and now < self._token_expires_at - TOKEN_REFRESH_SKEW:
                return self._token
            async with httpx.AsyncClient(timeout=15) as c:
                r = await c.post(
                    OPENSKY_TOKEN_URL,
                    data={
                        "grant_type": "client_credentials",
                        "client_id": self.client_id,
                        "client_secret": self.client_secret,
                    },
                )
                r.raise_for_status()
                payload = r.json()
            self._token = payload["access_token"]
            self._token_expires_at = now + float(payload.get("expires_in", 1800))
            return self._token

    # -------- upstream call --------

    async def _fetch(self, icao24: str) -> dict[str, Any] | None:
        """Ask OpenSky for the most recent flight for this ICAO24."""
        token = await self._get_token()
        end = int(time.time()) + 3600  # allow for clock skew / still-airborne
        begin = end - LOOKUP_WINDOW_HOURS * 3600
        async with httpx.AsyncClient(timeout=20) as c:
            r = await c.get(
                OPENSKY_FLIGHTS_URL,
                params={"icao24": icao24, "begin": begin, "end": end},
                headers={"Authorization": f"Bearer {token}"},
            )
        if r.status_code == 404:
            return None  # OpenSky returns 404 for "no flights in range"
        r.raise_for_status()
        flights = r.json() or []
        if not flights:
            return None
        # Most recent first (OpenSky returns chronologically).
        latest = flights[-1]
        return {
            "origin": latest.get("estDepartureAirport"),
            "destination": latest.get("estArrivalAirport"),
            "callsign": (latest.get("callsign") or "").strip() or None,
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
                json.dump({"cache": self._cache}, fh, separators=(",", ":"))
            tmp.replace(self.cache_path)
        except Exception as e:
            log.warning("couldn't persist flight-route cache: %s", e)
