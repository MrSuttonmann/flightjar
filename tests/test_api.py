"""HTTP endpoint smoke tests (FastAPI TestClient)."""

from fastapi.testclient import TestClient

from app import main


def _client() -> TestClient:
    # Use a transport-only client so we don't trigger the lifespan (which would
    # try to open a real BEAST socket).
    return TestClient(main.app, raise_server_exceptions=True)


def test_healthz_reports_disconnected_when_beast_is_down():
    main.stats.beast_connected = False
    with _client() as c:
        r = c.get("/healthz")
    assert r.status_code == 503
    assert r.json() == {"status": "disconnected"}


def test_healthz_reports_ok_when_connected():
    main.stats.beast_connected = True
    try:
        with _client() as c:
            r = c.get("/healthz")
        assert r.status_code == 200
        assert r.json() == {"status": "ok"}
    finally:
        main.stats.beast_connected = False


def test_metrics_has_prometheus_exposition_format():
    main.stats.beast_connected = True
    main.stats.frames = 42
    try:
        with _client() as c:
            r = c.get("/metrics")
        assert r.status_code == 200
        assert "text/plain" in r.headers["content-type"]
        body = r.text
        assert "# HELP flightjar_frames_total" in body
        assert "# TYPE flightjar_frames_total counter" in body
        assert "flightjar_frames_total 42" in body
        assert "flightjar_beast_connected 1" in body
    finally:
        main.stats.beast_connected = False
        main.stats.frames = 0


def test_index_injects_asset_content_hashes():
    # The /static/app.css and /static/app.js URLs should each carry a
    # distinct short content hash, so browsers re-fetch them after a deploy
    # even with aggressive caching headers.
    import re

    with _client() as c:
        r = c.get("/")
    assert r.status_code == 200
    body = r.text
    m_css = re.search(r"app\.css\?v=([0-9a-f]{12})", body)
    m_js = re.search(r"app\.js\?v=([0-9a-f]{12})", body)
    assert m_css and m_js, body
    # Placeholders must have been substituted (no literal __CSS_V__ / __JS_V__).
    assert "__CSS_V__" not in body
    assert "__JS_V__" not in body


def test_stats_includes_beast_connected_flag():
    main.stats.beast_connected = False
    with _client() as c:
        r = c.get("/api/stats")
    assert r.status_code == 200
    assert r.json()["beast_connected"] is False
