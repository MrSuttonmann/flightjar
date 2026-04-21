"""Tests for the alert watcher (watchlist + emergency fan-out)."""

import asyncio

from app.alerts import EMERGENCY_COOLDOWN_S, WATCHLIST_COOLDOWN_S, AlertWatcher
from app.watchlist import WatchlistStore


class _FakeNotifier:
    """Minimal stand-in for NotifierDispatcher — records dispatch calls
    so tests can assert on what got fired."""

    def __init__(self, enabled: bool = True):
        self._enabled = enabled
        self.calls: list[dict] = []

    @property
    def enabled(self) -> bool:
        return self._enabled

    async def dispatch(self, title, body, *, category, level="info", url=None, photo_url=None):
        self.calls.append(
            {
                "title": title,
                "body": body,
                "category": category,
                "level": level,
                "url": url,
                "photo_url": photo_url,
            }
        )


def _snap(*aircraft) -> dict:
    return {"aircraft": list(aircraft), "airports": {}}


def _ac(icao="abc123", **kw) -> dict:
    base = {"icao": icao}
    base.update(kw)
    return base


def test_watcher_no_ops_when_notifier_disabled():
    wl = WatchlistStore()
    wl.replace(["abc123"])
    notifier = _FakeNotifier(enabled=False)
    w = AlertWatcher(wl, notifier)  # type: ignore[arg-type]
    asyncio.run(w.observe(_snap(_ac("abc123", callsign="BAW1"))))
    assert notifier.calls == []


def test_watchlist_appearance_fires_once():
    wl = WatchlistStore()
    wl.replace(["abc123"])
    notifier = _FakeNotifier()
    w = AlertWatcher(wl, notifier)  # type: ignore[arg-type]
    asyncio.run(w.observe(_snap(_ac("abc123", callsign="BAW1"))))
    assert len(notifier.calls) == 1
    assert notifier.calls[0]["title"].startswith("Watchlist:")
    assert notifier.calls[0]["level"] == "info"


def test_watchlist_respects_cooldown():
    wl = WatchlistStore()
    wl.replace(["abc123"])
    notifier = _FakeNotifier()
    w = AlertWatcher(wl, notifier)  # type: ignore[arg-type]
    # Back-to-back observations for the same tail should fire once.
    asyncio.run(w.observe(_snap(_ac("abc123", callsign="BAW1"))))
    asyncio.run(w.observe(_snap(_ac("abc123", callsign="BAW1"))))
    assert len(notifier.calls) == 1


def test_watchlist_refires_after_cooldown():
    wl = WatchlistStore()
    wl.replace(["abc123"])
    notifier = _FakeNotifier()
    w = AlertWatcher(wl, notifier)  # type: ignore[arg-type]
    # First pass — fires. Roll the cooldown clock backwards on the
    # internal map by the cooldown window + a second, then second
    # observation should fire again.
    asyncio.run(w.observe(_snap(_ac("abc123"))))
    w._watchlist_cooldown["abc123"] -= WATCHLIST_COOLDOWN_S + 1
    asyncio.run(w.observe(_snap(_ac("abc123"))))
    assert len(notifier.calls) == 2


def test_emergency_squawk_fires_with_emergency_level():
    wl = WatchlistStore()
    notifier = _FakeNotifier()
    w = AlertWatcher(wl, notifier)  # type: ignore[arg-type]
    asyncio.run(w.observe(_snap(_ac("def456", callsign="SAR01", squawk="7700"))))
    assert len(notifier.calls) == 1
    call = notifier.calls[0]
    assert "7700" in call["title"]
    assert call["level"] == "emergency"
    assert "general emergency" in call["body"].lower()


def test_emergency_cooldown_is_independent_of_watchlist_cooldown():
    wl = WatchlistStore()
    wl.replace(["abc123"])
    notifier = _FakeNotifier()
    w = AlertWatcher(wl, notifier)  # type: ignore[arg-type]
    # Same tail on watchlist AND squawking 7700: both alerts fire on
    # the first observation, and each has its own cooldown timer.
    asyncio.run(w.observe(_snap(_ac("abc123", callsign="BAW1", squawk="7700"))))
    assert len(notifier.calls) == 2
    levels = {c["level"] for c in notifier.calls}
    assert levels == {"info", "emergency"}
    # Advance only the emergency cooldown; watchlist stays cool.
    w._emergency_cooldown["abc123"] -= EMERGENCY_COOLDOWN_S + 1
    asyncio.run(w.observe(_snap(_ac("abc123", callsign="BAW1", squawk="7700"))))
    assert len(notifier.calls) == 3
    assert notifier.calls[-1]["level"] == "emergency"


def test_non_emergency_squawks_dont_fire():
    wl = WatchlistStore()
    notifier = _FakeNotifier()
    w = AlertWatcher(wl, notifier)  # type: ignore[arg-type]
    for squawk in ("1200", "2000", "7000"):
        asyncio.run(w.observe(_snap(_ac("abc123", squawk=squawk))))
    assert notifier.calls == []


def test_dispatch_carries_category_tag():
    """The watcher hands each alert a `category` so per-channel gates
    in the dispatcher can decide whether to forward it."""
    wl = WatchlistStore()
    wl.replace(["abc123"])
    notifier = _FakeNotifier()
    w = AlertWatcher(wl, notifier)  # type: ignore[arg-type]
    asyncio.run(w.observe(_snap(_ac("abc123", callsign="BAW1", squawk="7700"))))
    categories = {c["category"] for c in notifier.calls}
    assert categories == {"watchlist", "emergency"}


def test_body_carries_identity_altitude_and_route():
    wl = WatchlistStore()
    wl.replace(["abc123"])
    notifier = _FakeNotifier()
    w = AlertWatcher(wl, notifier)  # type: ignore[arg-type]
    asyncio.run(
        w.observe(
            _snap(
                _ac(
                    "abc123",
                    callsign="BAW1",
                    registration="G-XWBC",
                    type_long="BOEING 787-9",
                    altitude=38000,
                    origin="EGLL",
                    destination="KJFK",
                )
            )
        )
    )
    body = notifier.calls[0]["body"]
    assert "G-XWBC" in body
    assert "BOEING 787-9" in body
    assert "38,000 ft" in body
    assert "EGLL → KJFK" in body


def test_flightaware_url_is_attached_for_deep_linking():
    wl = WatchlistStore()
    wl.replace(["abc123"])
    notifier = _FakeNotifier()
    w = AlertWatcher(wl, notifier)  # type: ignore[arg-type]
    asyncio.run(w.observe(_snap(_ac("abc123", callsign="BAW1"))))
    assert notifier.calls[0]["url"] == "https://flightaware.com/live/modes/abc123/redirect"


def test_empty_or_missing_icao_is_skipped():
    wl = WatchlistStore()
    notifier = _FakeNotifier()
    w = AlertWatcher(wl, notifier)  # type: ignore[arg-type]
    asyncio.run(w.observe(_snap({"icao": ""}, {"icao": None, "squawk": "7700"})))
    assert notifier.calls == []
