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


def test_airports_accepts_valid_bbox():
    with _client() as c:
        r = c.get(
            "/api/airports",
            params={"min_lat": 50, "min_lon": -2, "max_lat": 52, "max_lon": 1},
        )
    assert r.status_code == 200
    assert isinstance(r.json(), list)


def test_airports_rejects_out_of_range_latitude():
    with _client() as c:
        r = c.get(
            "/api/airports",
            params={"min_lat": -91, "min_lon": -2, "max_lat": 52, "max_lon": 1},
        )
    assert r.status_code == 400


def test_airports_rejects_inverted_latitude():
    # min_lat > max_lat is a caller error — the bbox would be empty anyway.
    with _client() as c:
        r = c.get(
            "/api/airports",
            params={"min_lat": 52, "min_lon": -2, "max_lat": 50, "max_lon": 1},
        )
    assert r.status_code == 400


def test_airports_rejects_out_of_range_longitude():
    with _client() as c:
        r = c.get(
            "/api/airports",
            params={"min_lat": 50, "min_lon": -200, "max_lat": 52, "max_lon": 1},
        )
    assert r.status_code == 400


def test_airports_allows_antimeridian_wrap():
    # min_lon > max_lon is valid — it means the bbox crosses the
    # antimeridian (e.g. Pacific coverage). Must not trigger the
    # inverted-latitude error by accident.
    with _client() as c:
        r = c.get(
            "/api/airports",
            params={"min_lat": 50, "min_lon": 170, "max_lat": 52, "max_lon": -170},
        )
    assert r.status_code == 200


# -------- /api/watchlist --------


def _reset_watchlist():
    main.watchlist_store.replace([])


def test_watchlist_get_round_trips_empty():
    _reset_watchlist()
    with _client() as c:
        r = c.get("/api/watchlist")
    assert r.status_code == 200
    assert r.json() == {"icao24s": [], "last_seen": {}}


def test_watchlist_post_replaces_and_normalises():
    _reset_watchlist()
    try:
        with _client() as c:
            r = c.post(
                "/api/watchlist",
                json={"icao24s": ["ABC123", " def456 ", "xyz!!!", "00aabb"]},
            )
        assert r.status_code == 200
        body = r.json()
        # Invalid entries are dropped, valid ones lowercased + sorted.
        assert body["icao24s"] == ["00aabb", "abc123", "def456"]
        assert body["last_seen"] == {}
        # Persisted to the server-side store — the GET endpoint returns
        # the same list.
        with _client() as c:
            r2 = c.get("/api/watchlist")
        assert r2.json()["icao24s"] == ["00aabb", "abc123", "def456"]
    finally:
        _reset_watchlist()


def test_watchlist_get_returns_last_seen_map():
    _reset_watchlist()
    try:
        main.watchlist_store.replace(["abc123"])
        main.watchlist_store.record_seen("abc123", 1_700_000_000.0)
        with _client() as c:
            r = c.get("/api/watchlist")
        body = r.json()
        assert body["icao24s"] == ["abc123"]
        assert body["last_seen"] == {"abc123": 1_700_000_000.0}
    finally:
        _reset_watchlist()


def test_watchlist_post_rejects_bad_body_shape():
    _reset_watchlist()
    with _client() as c:
        # Missing icao24s key.
        r = c.post("/api/watchlist", json={"ids": ["abc123"]})
    assert r.status_code == 400


def test_watchlist_post_rejects_oversized_payload():
    _reset_watchlist()
    payload = {"icao24s": ["abc123"] * 20_000}
    with _client() as c:
        r = c.post("/api/watchlist", json=payload)
    assert r.status_code == 413


# -------- /api/notifications --------


def _reset_notifications():
    main.notifications_config.replace({"channels": []})


def _sample_channel(**kw):
    base = {
        "type": "webhook",
        "name": "HA",
        "url": "https://ha.example/hook",
        "enabled": True,
        "watchlist_enabled": True,
        "emergency_enabled": True,
    }
    base.update(kw)
    return base


def test_notifications_config_round_trip():
    _reset_notifications()
    try:
        with _client() as c:
            r1 = c.get("/api/notifications/config")
        assert r1.status_code == 200
        assert r1.json() == {"version": 1, "channels": []}
        with _client() as c:
            r2 = c.post(
                "/api/notifications/config",
                json={"channels": [_sample_channel(name="Primary")]},
            )
        assert r2.status_code == 200
        saved = r2.json()["channels"]
        assert len(saved) == 1
        assert saved[0]["id"]  # server assigned one
        assert saved[0]["url"] == "https://ha.example/hook"
    finally:
        _reset_notifications()


def test_notifications_config_rejects_non_object_body():
    # FastAPI's own type-validation rejects non-dicts at 422; either
    # 400 (our hand-rolled check) or 422 (framework) is an acceptable
    # "your body is wrong" response.
    with _client() as c:
        r = c.post("/api/notifications/config", json=["not", "an", "object"])
    assert r.status_code in (400, 422)


def test_notifications_config_accepts_missing_channels_key():
    _reset_notifications()
    # Dict missing `channels` is NOT rejected — replace() just treats
    # it as an empty list. This keeps the upgrade path smooth when a
    # future client sends a different shape.
    with _client() as c:
        r = c.post("/api/notifications/config", json={"other": "stuff"})
    assert r.status_code == 200
    assert r.json()["channels"] == []


def test_notifications_config_rejects_oversized_payload():
    with _client() as c:
        r = c.post(
            "/api/notifications/config",
            json={"channels": [_sample_channel()] * 200},
        )
    assert r.status_code == 413


def test_notifications_test_unknown_channel_returns_404():
    _reset_notifications()
    with _client() as c:
        r = c.post("/api/notifications/test/nope")
    assert r.status_code == 404
