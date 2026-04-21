"""Tests for the notifications config store."""

import json
from pathlib import Path

from app.notifications_config import NotificationsConfigStore, channel_is_ready


def _telegram(bot_token="123:abc", chat_id="42", **kw):
    base = {
        "type": "telegram",
        "name": "Phone",
        "enabled": True,
        "watchlist_enabled": True,
        "emergency_enabled": True,
        "bot_token": bot_token,
        "chat_id": chat_id,
    }
    base.update(kw)
    return base


def _ntfy(url="https://ntfy.sh/x", **kw):
    base = {"type": "ntfy", "name": "Primary", "url": url}
    base.update(kw)
    return base


def _webhook(url="https://hook.example", **kw):
    base = {"type": "webhook", "name": "HA", "url": url}
    base.update(kw)
    return base


def test_empty_store_returns_empty_config(tmp_path: Path):
    s = NotificationsConfigStore(path=tmp_path / "notifications.json")
    assert s.get() == {"version": 1, "channels": []}
    assert s.channels() == []


def test_replace_assigns_ids_and_persists(tmp_path: Path):
    path = tmp_path / "notifications.json"
    s1 = NotificationsConfigStore(path=path)
    s1.replace({"channels": [_telegram(), _ntfy(), _webhook()]})
    # Reload from disk — all three survived and each got an id.
    s2 = NotificationsConfigStore(path=path)
    got = s2.channels()
    assert len(got) == 3
    ids = {c["id"] for c in got}
    assert len(ids) == 3
    assert all(c["id"] for c in got)
    assert [c["type"] for c in got] == ["telegram", "ntfy", "webhook"]


def test_replace_drops_unknown_types(tmp_path: Path):
    s = NotificationsConfigStore(path=tmp_path / "notifications.json")
    s.replace({"channels": [_telegram(), {"type": "carrier_pigeon"}]})
    assert [c["type"] for c in s.channels()] == ["telegram"]


def test_replace_strips_non_allowed_fields(tmp_path: Path):
    s = NotificationsConfigStore(path=tmp_path / "notifications.json")
    # `secret_admin_flag` isn't in the webhook allow-list — drop it.
    s.replace({"channels": [{**_webhook(), "secret_admin_flag": True}]})
    saved = s.channels()[0]
    assert "secret_admin_flag" not in saved
    assert saved["url"] == "https://hook.example"


def test_replace_regenerates_duplicate_ids(tmp_path: Path):
    s = NotificationsConfigStore(path=tmp_path / "notifications.json")
    s.replace(
        {
            "channels": [
                _telegram(id="same"),
                _ntfy(id="same"),
            ]
        }
    )
    ids = {c["id"] for c in s.channels()}
    # Second entry got a fresh id so they don't collide.
    assert len(ids) == 2


def test_replace_keeps_explicit_ids(tmp_path: Path):
    s = NotificationsConfigStore(path=tmp_path / "notifications.json")
    s.replace({"channels": [_telegram(id="abc12345")]})
    assert s.channels()[0]["id"] == "abc12345"


def test_replace_defaults_bool_flags(tmp_path: Path):
    s = NotificationsConfigStore(path=tmp_path / "notifications.json")
    # Missing bool flags default to True (helpful defaults so users
    # don't have to tick three checkboxes every time they add an entry).
    s.replace({"channels": [{"type": "ntfy", "url": "https://ntfy.sh/x"}]})
    c = s.channels()[0]
    assert c["enabled"] is True
    assert c["watchlist_enabled"] is True
    assert c["emergency_enabled"] is True


def test_replace_accepts_bare_list(tmp_path: Path):
    s = NotificationsConfigStore(path=tmp_path / "notifications.json")
    # A list at the top-level is also acceptable (some clients omit the
    # wrapping dict).
    s.replace([_telegram()])
    assert len(s.channels()) == 1


def test_replace_is_idempotent(tmp_path: Path):
    path = tmp_path / "notifications.json"
    s = NotificationsConfigStore(path=path)
    s.replace({"channels": [_webhook(id="w1")]})
    mtime1 = path.stat().st_mtime_ns
    s.replace({"channels": [_webhook(id="w1")]})
    mtime2 = path.stat().st_mtime_ns
    assert mtime1 == mtime2


def test_find_returns_deep_copy(tmp_path: Path):
    s = NotificationsConfigStore(path=tmp_path / "notifications.json")
    s.replace({"channels": [_telegram(id="abc")]})
    got = s.find("abc")
    assert got is not None
    got["bot_token"] = "tampered"
    # Mutation of the returned dict must not leak into the store.
    assert s.find("abc")["bot_token"] == "123:abc"


def test_find_missing_id_returns_none(tmp_path: Path):
    s = NotificationsConfigStore(path=tmp_path / "notifications.json")
    assert s.find("nope") is None


def test_channel_is_ready_checks_required_fields():
    assert channel_is_ready(_telegram()) is True
    assert channel_is_ready(_telegram(bot_token="")) is False
    assert channel_is_ready(_telegram(chat_id="")) is False
    assert channel_is_ready(_ntfy()) is True
    assert channel_is_ready(_ntfy(url="")) is False
    assert channel_is_ready(_webhook()) is True
    assert channel_is_ready(_webhook(url="")) is False
    # Unknown type is never ready.
    assert channel_is_ready({"type": "carrier_pigeon"}) is False


def test_corrupt_file_is_ignored(tmp_path: Path):
    path = tmp_path / "notifications.json"
    path.write_text("not json", encoding="utf-8")
    s = NotificationsConfigStore(path=path)
    assert s.channels() == []


def test_missing_channels_key_is_ignored(tmp_path: Path):
    path = tmp_path / "notifications.json"
    path.write_text(json.dumps({"version": 1, "other": "stuff"}), encoding="utf-8")
    s = NotificationsConfigStore(path=path)
    assert s.channels() == []
