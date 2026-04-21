"""Snapshot-driven alert generator.

Runs on every snapshot tick and fires notifications through the
NotifierDispatcher when:

* An aircraft on the watchlist appears in the snapshot — with a
  per-tail cooldown so a plane flickering on the edge of coverage
  doesn't spam. Default 30 min.

* An aircraft squawks an emergency code (7500 / 7600 / 7700). These
  have a shorter cooldown (5 min) because they're inherently critical
  and the operator usually wants to know of every distinct event.

Per-category channel gating lives in NotifierDispatcher — a channel
can opt in to watchlist only, emergency only, or both. This file
decides *whether* to alert; the dispatcher decides *where*.
"""

from __future__ import annotations

import logging
import time
from typing import Any

from .notifications import NotifierDispatcher
from .watchlist import WatchlistStore

log = logging.getLogger("beast.alerts")

EMERGENCY_SQUAWKS: dict[str, str] = {
    "7500": "hijack",
    "7600": "radio failure",
    "7700": "general emergency",
}

# Cooldown windows — tuneable constants, not configuration. Watchlist
# reappearance is a "heads-up" event so 30 min prevents a plane that
# briefly dips out of coverage from firing twice. Emergency is
# tighter because it's actionable.
WATCHLIST_COOLDOWN_S = 30 * 60
EMERGENCY_COOLDOWN_S = 5 * 60


class AlertWatcher:
    """Per-snapshot observer that decides when to fire notifications."""

    def __init__(
        self,
        watchlist: WatchlistStore,
        notifier: NotifierDispatcher,
    ) -> None:
        self._watchlist = watchlist
        self._notifier = notifier
        # icao24 -> last-alerted unix ts
        self._watchlist_cooldown: dict[str, float] = {}
        self._emergency_cooldown: dict[str, float] = {}

    async def observe(self, snap: dict[str, Any]) -> None:
        """Process one snapshot; fire any alerts whose cooldowns expired."""
        if not self._notifier.enabled:
            return
        now = time.time()
        for ac in snap.get("aircraft", []):
            icao_raw = ac.get("icao") or ""
            if not icao_raw:
                continue
            icao = icao_raw.lower()
            if self._watchlist.has(icao):
                await self._maybe_fire(
                    icao,
                    ac,
                    self._watchlist_cooldown,
                    WATCHLIST_COOLDOWN_S,
                    now,
                    self._build_watchlist_alert,
                    "watchlist",
                )
            squawk = ac.get("squawk")
            if squawk in EMERGENCY_SQUAWKS:
                await self._maybe_fire(
                    icao,
                    ac,
                    self._emergency_cooldown,
                    EMERGENCY_COOLDOWN_S,
                    now,
                    self._build_emergency_alert,
                    "emergency",
                )

    # -------- dispatch helpers --------

    async def _maybe_fire(
        self,
        icao: str,
        ac: dict,
        cooldown_map: dict[str, float],
        cooldown_s: float,
        now: float,
        build,
        category: str,
    ) -> None:
        last = cooldown_map.get(icao, 0.0)
        if now - last < cooldown_s:
            return
        cooldown_map[icao] = now
        title, body, level = build(ac)
        await self._notifier.dispatch(
            title,
            body,
            category=category,
            level=level,
            url=self._flightaware_url(icao),
        )

    @staticmethod
    def _build_watchlist_alert(ac: dict) -> tuple[str, str, str]:
        icao = (ac.get("icao") or "").lower()
        label = ac.get("callsign") or ac.get("registration") or icao.upper()
        title = f"Watchlist: {label}"
        body = " · ".join(p for p in _alert_facts(ac) if p)
        return title, body or "in range", "info"

    @staticmethod
    def _build_emergency_alert(ac: dict) -> tuple[str, str, str]:
        icao = (ac.get("icao") or "").lower()
        label = ac.get("callsign") or ac.get("registration") or icao.upper()
        squawk = ac.get("squawk") or "????"
        reason = EMERGENCY_SQUAWKS.get(squawk, "emergency")
        title = f"⚠️ Emergency {squawk} — {label}"
        parts = [f"Squawking {squawk} ({reason})"]
        parts.extend(p for p in _alert_facts(ac) if p)
        return title, " · ".join(parts), "emergency"

    @staticmethod
    def _flightaware_url(icao: str) -> str:
        return f"https://flightaware.com/live/modes/{icao}/redirect"


def _alert_facts(ac: dict) -> list[str]:
    """Compact "who/where/what" summary used inside both alert bodies."""
    bits: list[str] = []
    if ac.get("registration"):
        bits.append(str(ac["registration"]))
    if ac.get("type_long"):
        bits.append(str(ac["type_long"]))
    alt = ac.get("altitude")
    if alt is not None:
        bits.append(f"{alt:,} ft")
    origin = ac.get("origin")
    dest = ac.get("destination")
    if origin or dest:
        bits.append(f"{origin or '—'} → {dest or '—'}")
    return bits
