"""Tests for VFRMap cycle auto-discovery."""

import asyncio
import json
from pathlib import Path

import httpx

from app.vfrmap_cycle import CACHE_SCHEMA_VERSION, VfrmapCycle

# ---- extract_mapjs_path ----


def test_extract_mapjs_path_finds_src_with_cachebuster():
    html = '<script type="text/javascript" src="js/map.js?7"></script>'
    assert VfrmapCycle.extract_mapjs_path(html) == "js/map.js?7"


def test_extract_mapjs_path_handles_absolute_src():
    html = '<script src="/js/map.js?123"></script>'
    assert VfrmapCycle.extract_mapjs_path(html) == "/js/map.js?123"


def test_extract_mapjs_path_none_when_missing():
    assert VfrmapCycle.extract_mapjs_path("<html></html>") is None


# ---- extract_cycle ----


def test_extract_cycle_picks_newest_match():
    js = """
      var a = 'old';
      var f = '20240627';  // last cycle
      var g = '20250807';  // current cycle
    """
    assert VfrmapCycle.extract_cycle(js) == "20250807"


def test_extract_cycle_ignores_unrelated_numbers():
    # Phone numbers / IDs / etc. shouldn't confuse the regex — the lookarounds
    # require the date to not be part of a longer digit run.
    js = "var phone = '2025080712345'; var cycle = '20260319';"
    assert VfrmapCycle.extract_cycle(js) == "20260319"


def test_extract_cycle_returns_none_when_no_match():
    assert VfrmapCycle.extract_cycle("var a = 'nope';") is None


def test_extract_cycle_rejects_implausible_dates():
    # Year 1999 is before VFRMap existed; year 9999 is obvious noise.
    assert VfrmapCycle.extract_cycle("var f = '19990101';") is None
    # Note: /19990101/ won't even match the regex because the year-prefix
    # is 20xx-only, so this is a belt-and-braces check.
    # Year 3000 is well above our MAX_YEARS_AHEAD threshold.
    assert VfrmapCycle.extract_cycle("var f = '30000101';") is None


# ---- cache round-trip ----


def test_cache_round_trip(tmp_path: Path):
    cache = tmp_path / "vfrmap_cycle.json"
    c1 = VfrmapCycle(cache)
    c1._date = "20250807"
    c1._save_cache("20250807")
    assert cache.exists()
    data = json.loads(cache.read_text())
    assert data["schema_version"] == CACHE_SCHEMA_VERSION
    assert data["date"] == "20250807"

    c2 = VfrmapCycle(cache)
    c2.load_cache()
    assert c2.current_date() == "20250807"


def test_cache_wrong_schema_is_ignored(tmp_path: Path):
    cache = tmp_path / "vfrmap_cycle.json"
    cache.write_text(json.dumps({"schema_version": 99, "date": "20250807"}))
    c = VfrmapCycle(cache)
    c.load_cache()
    assert c.current_date() is None


def test_override_wins_over_cache(tmp_path: Path):
    cache = tmp_path / "vfrmap_cycle.json"
    cache.write_text(json.dumps({"schema_version": CACHE_SCHEMA_VERSION, "date": "20200101"}))
    c = VfrmapCycle(cache, override="20300101")
    c.load_cache()  # should be a no-op when override is set
    assert c.current_date() == "20300101"


# ---- discover end-to-end (mocked transport) ----


def _two_step_handler(js_text: str, mapjs_path: str = "js/map.js?7"):
    """Mock transport that serves a minimal vfrmap.com homepage and the
    matching map.js from a fake origin."""
    homepage = f'<html><script src="{mapjs_path}"></script></html>'

    def handler(request: httpx.Request) -> httpx.Response:
        # Strip the leading slash on map.js since base_url handles it.
        if request.url.path == "/" or request.url.path == "":
            return httpx.Response(200, text=homepage)
        if request.url.path.endswith("map.js"):
            return httpx.Response(200, text=js_text)
        return httpx.Response(404, text="unexpected path")

    return handler


def test_discover_parses_homepage_and_mapjs(tmp_path: Path):
    handler = _two_step_handler("var f = '20260319';")
    cache = tmp_path / "vfrmap_cycle.json"
    c = VfrmapCycle(cache)
    c._client = httpx.AsyncClient(
        base_url="https://vfrmap.com/",
        transport=httpx.MockTransport(handler),
    )

    result = asyncio.run(c.discover())
    assert result == "20260319"
    assert c.current_date() == "20260319"
    assert json.loads(cache.read_text())["date"] == "20260319"

    asyncio.run(c.aclose())


def test_discover_keeps_previous_on_homepage_failure(tmp_path: Path):
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(500, text="oops")

    cache = tmp_path / "vfrmap_cycle.json"
    c = VfrmapCycle(cache)
    c._date = "20250807"
    c._client = httpx.AsyncClient(
        base_url="https://vfrmap.com/",
        transport=httpx.MockTransport(handler),
    )

    result = asyncio.run(c.discover())
    assert result is None
    assert c.current_date() == "20250807"

    asyncio.run(c.aclose())


def test_discover_keeps_previous_when_mapjs_has_no_date(tmp_path: Path):
    handler = _two_step_handler("var x = 'nothing useful';")
    cache = tmp_path / "vfrmap_cycle.json"
    c = VfrmapCycle(cache)
    c._date = "20250807"
    c._client = httpx.AsyncClient(
        base_url="https://vfrmap.com/",
        transport=httpx.MockTransport(handler),
    )

    result = asyncio.run(c.discover())
    assert result is None
    assert c.current_date() == "20250807"

    asyncio.run(c.aclose())
