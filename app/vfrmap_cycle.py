"""Auto-discover the current VFRMap.com chart cycle date.

VFRMap.com serves FAA IFR Low / IFR High enroute tiles under date-prefixed
paths like `/20260410/tiles/ifrlc/{z}/{y}/{x}.jpg`. Cycles rotate every 28
days and there's no stable `/current/` redirector, so a hardcoded env var
goes stale silently. This module fetches vfrmap.com's homepage on startup,
extracts the most recent cycle date from the embedded tile URLs, caches it
to disk so restarts don't need network, and refreshes periodically in the
background.

An optional `override` (from env) pins to a specific date — useful for
offline/air-gapped deployments and for reproducing bug reports against a
known cycle.
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import re
import time
from datetime import date, datetime
from pathlib import Path

import httpx

log = logging.getLogger("beast.vfrmap_cycle")

# VFRMap's homepage embeds tile URLs with the current cycle date. We
# fetch the root page and pick the newest YYYYMMDD that appears in a
# `/YYYYMMDD/tiles/<chart>/` path — that's robust to incidental `20…`
# numbers elsewhere in the HTML.
INDEX_URL = "https://vfrmap.com/"
# VFRMap's homepage doesn't contain the cycle date directly — it loads
# `js/map.js` which hardcodes the current cycle as a string literal.
# We fetch the homepage, find the map.js path (including their
# cache-buster query string), then fetch that and pull the date out.
MAPJS_RE = re.compile(r'src=["\']([^"\']*?js/map\.js[^"\']*?)["\']')
# Any 8-digit date-like sequence in map.js. The cycle currently lives in
# `var f='YYYYMMDD';`; a looser regex hedges against future renames.
CYCLE_RE = re.compile(r"(?<![0-9])(20\d{6})(?![0-9])")

REQUEST_TIMEOUT = 10.0
REFRESH_INTERVAL_S = 6 * 3600  # 6 h — new cycles become available on a known schedule
CACHE_SCHEMA_VERSION = 1

# Sanity bounds on the extracted date. The first date VFRMap ever served
# is well before 2010, and anything more than a year in the future is
# almost certainly noise (or a typo on their end).
_MIN_YEAR = 2010
_MAX_YEARS_AHEAD = 1


class VfrmapCycle:
    """Discovers + caches VFRMap's current chart cycle date.

    Use `load_cache()` at startup to populate from disk synchronously,
    then `discover()` in the background to refresh. `current_date()` is
    always safe to call — it returns None if no cycle has been learned.
    """

    def __init__(self, cache_path: Path | None, override: str = "") -> None:
        self._cache_path = cache_path
        self._override = override.strip()
        self._date: str | None = self._override or None
        self._client: httpx.AsyncClient | None = None

    def current_date(self) -> str | None:
        """Return the current YYYYMMDD cycle, or None if not yet known."""
        return self._date

    def load_cache(self) -> None:
        """Populate from the on-disk cache if present. No network."""
        if self._override or self._cache_path is None:
            return
        if not self._cache_path.exists():
            return
        try:
            raw = self._cache_path.read_text(encoding="utf-8")
            data = json.loads(raw)
            if data.get("schema_version") != CACHE_SCHEMA_VERSION:
                return
            candidate = data.get("date")
            if isinstance(candidate, str) and self._is_valid_cycle(candidate):
                self._date = candidate
                log.info("loaded cached VFRMap cycle %s", candidate)
        except Exception as e:
            log.warning("failed to read VFRMap cycle cache: %s", e)

    def _save_cache(self, cycle: str) -> None:
        if self._cache_path is None:
            return
        try:
            self._cache_path.parent.mkdir(parents=True, exist_ok=True)
            payload = {
                "schema_version": CACHE_SCHEMA_VERSION,
                "date": cycle,
                "discovered_at": int(time.time()),
            }
            tmp = self._cache_path.with_suffix(self._cache_path.suffix + ".tmp")
            tmp.write_text(json.dumps(payload), encoding="utf-8")
            tmp.replace(self._cache_path)
        except Exception as e:
            log.warning("failed to write VFRMap cycle cache: %s", e)

    @staticmethod
    def _is_valid_cycle(cycle: str) -> bool:
        """Sanity-check a YYYYMMDD candidate — real date, plausibly recent."""
        try:
            d = datetime.strptime(cycle, "%Y%m%d").date()
        except ValueError:
            return False
        today = date.today()
        if d.year < _MIN_YEAR:
            return False
        return d.year <= today.year + _MAX_YEARS_AHEAD

    @classmethod
    def extract_mapjs_path(cls, html: str) -> str | None:
        """Find the `js/map.js` path (with cache-buster) referenced in the
        homepage. Needed because VFRMap bumps the `?N` suffix when they
        ship a new bundle — scraping a fixed URL would serve stale JS."""
        m = MAPJS_RE.search(html)
        return m.group(1) if m else None

    @classmethod
    def extract_cycle(cls, js: str) -> str | None:
        """Pull the newest plausible cycle date from the map.js source.

        The cycle currently lives in a `var f='YYYYMMDD';` literal; the
        regex just looks for any standalone 8-digit `20…` token and
        takes the chronologically latest one that passes a sanity check.
        """
        hits = {m.group(1) for m in CYCLE_RE.finditer(js)}
        valid = [h for h in hits if cls._is_valid_cycle(h)]
        if not valid:
            return None
        # YYYYMMDD is lexically monotonic in chronological order.
        return max(valid)

    async def discover(self) -> str | None:
        """Two-step scrape of vfrmap.com → current chart cycle.

        1. GET the homepage, find the `js/map.js` URL.
        2. GET that JS file, pull the hardcoded cycle date.

        Returns the discovered cycle, or None on failure. A failure
        doesn't clobber the previously known date — we keep serving the
        last-known-good cycle until discovery recovers.
        """
        if self._override:
            return self._override
        try:
            if self._client is None:
                self._client = httpx.AsyncClient(
                    timeout=REQUEST_TIMEOUT,
                    base_url=INDEX_URL,
                    follow_redirects=True,
                )
            index_resp = await self._client.get("/")
            index_resp.raise_for_status()
            mapjs_path = self.extract_mapjs_path(index_resp.text)
            if mapjs_path is None:
                log.warning("VFRMap homepage has no map.js reference")
                return None
            mapjs_resp = await self._client.get(mapjs_path)
            mapjs_resp.raise_for_status()
            cycle = self.extract_cycle(mapjs_resp.text)
        except Exception as e:
            log.warning("VFRMap cycle discovery failed: %s", e)
            return None
        if cycle is None:
            log.warning("VFRMap map.js returned no recognisable cycle dates")
            return None
        if cycle != self._date:
            log.info(
                "VFRMap cycle %s (previous: %s)",
                cycle,
                self._date or "unknown",
            )
        self._date = cycle
        self._save_cache(cycle)
        return cycle

    async def refresher(self) -> None:
        """Run `discover()` every REFRESH_INTERVAL_S until cancelled."""
        if self._override:
            # Pinned to an env override — no point polling.
            return
        while True:
            try:
                await asyncio.sleep(REFRESH_INTERVAL_S)
                await self.discover()
            except asyncio.CancelledError:
                raise
            except Exception as e:
                log.warning("VFRMap cycle refresher error: %s", e)

    async def aclose(self) -> None:
        if self._client is not None:
            with contextlib.suppress(Exception):
                await self._client.aclose()
            self._client = None
