"""Tests for the notification dispatcher + channel implementations."""

import asyncio
from pathlib import Path
from unittest.mock import AsyncMock, MagicMock

import httpx
import pytest

from app.notifications import (
    NotifierDispatcher,
    NtfyNotifier,
    TelegramNotifier,
    WebhookNotifier,
    _channel_accepts,
    _instantiate,
    _tg_escape,
)
from app.notifications_config import NotificationsConfigStore


class _FakeResp:
    def __init__(self, status_code: int = 200):
        self.status_code = status_code

    def raise_for_status(self):
        if self.status_code >= 400:
            raise httpx.HTTPStatusError(
                "fail",
                request=httpx.Request("POST", "https://example/test"),
                response=httpx.Response(self.status_code),
            )


def _mock_client(status: int = 200):
    client = MagicMock()
    client.post = AsyncMock(return_value=_FakeResp(status))
    return client


def _store(tmp_path: Path, channels: list[dict] | None = None) -> NotificationsConfigStore:
    s = NotificationsConfigStore(path=tmp_path / "notifications.json")
    if channels:
        s.replace({"channels": channels})
    return s


def _telegram(bot_token="123:abc", chat_id="42", **kw):
    base = {
        "type": "telegram",
        "name": "Phone",
        "bot_token": bot_token,
        "chat_id": chat_id,
        "enabled": True,
        "watchlist_enabled": True,
        "emergency_enabled": True,
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


# -------- helpers --------


def test_tg_escape_preserves_plain_text():
    assert _tg_escape("Hello world") == "Hello world"


def test_tg_escape_escapes_all_reserved_markdownv2_chars():
    escaped = _tg_escape("_*[]()~`>#+-=|{}.!\\")
    for raw in "_*[]()~`>#+-=|{}.!\\":
        assert f"\\{raw}" in escaped, f"missing escape for {raw!r}"


# -------- TelegramNotifier --------


def test_telegram_uses_sendmessage_without_photo():
    client = _mock_client()
    n = TelegramNotifier("bot-token", "12345", lambda: client)
    asyncio.run(n.send("Watchlist: BAW123", "G-XWBC · Boeing 787"))
    client.post.assert_awaited_once()
    url, kwargs = client.post.call_args.args[0], client.post.call_args.kwargs
    assert url.endswith("/sendMessage")
    assert kwargs["json"]["chat_id"] == "12345"
    assert "MarkdownV2" in kwargs["json"]["parse_mode"]
    assert kwargs["json"]["disable_web_page_preview"] is True


def test_telegram_uses_sendphoto_when_photo_url_present():
    client = _mock_client()
    n = TelegramNotifier("bot-token", "12345", lambda: client)
    asyncio.run(
        n.send(
            "Watchlist: BAW123",
            "G-XWBC",
            photo_url="https://img.example/photo.jpg",
        )
    )
    url = client.post.call_args.args[0]
    assert url.endswith("/sendPhoto")
    payload = client.post.call_args.kwargs["json"]
    assert payload["photo"] == "https://img.example/photo.jpg"


def test_telegram_swallows_upstream_errors():
    client = _mock_client(500)
    n = TelegramNotifier("bot-token", "12345", lambda: client)
    # Must not raise — each channel logs its own failures.
    asyncio.run(n.send("title", "body"))


# -------- NtfyNotifier --------


def test_ntfy_sends_title_priority_tags_via_headers():
    client = _mock_client()
    n = NtfyNotifier("https://ntfy.sh/flightjar", None, lambda: client)
    asyncio.run(
        n.send(
            "Emergency 7700",
            "squawk",
            level="emergency",
            url="https://fa.example/ABC",
            photo_url="https://img.example/1.jpg",
        )
    )
    headers = client.post.call_args.kwargs["headers"]
    assert headers["Title"] == "Emergency 7700"
    assert headers["Priority"] == "urgent"
    assert "rotating_light" in headers["Tags"]
    assert headers["Click"].startswith("https://fa.example")
    assert headers["Attach"].startswith("https://img.example")
    assert client.post.call_args.kwargs["content"] == b"squawk"


def test_ntfy_adds_bearer_token_when_configured():
    client = _mock_client()
    n = NtfyNotifier("https://ntfy.sh/flightjar", "sekret", lambda: client)
    asyncio.run(n.send("t", "b"))
    assert client.post.call_args.kwargs["headers"]["Authorization"] == "Bearer sekret"


@pytest.mark.parametrize(
    "level,expected",
    [("info", "default"), ("warning", "high"), ("emergency", "urgent")],
)
def test_ntfy_priority_ladder(level: str, expected: str):
    client = _mock_client()
    n = NtfyNotifier("https://ntfy.sh/x", None, lambda: client)
    asyncio.run(n.send("t", "b", level=level))
    assert client.post.call_args.kwargs["headers"]["Priority"] == expected


# -------- WebhookNotifier --------


def test_webhook_posts_full_payload_shape():
    client = _mock_client()
    n = WebhookNotifier("https://example/hook", lambda: client)
    asyncio.run(
        n.send(
            "Watchlist: BAW123",
            "G-XWBC",
            level="warning",
            url="https://fa.example/ABC",
            photo_url="https://img.example/1.jpg",
        )
    )
    kwargs = client.post.call_args.kwargs
    assert kwargs["json"] == {
        "title": "Watchlist: BAW123",
        "body": "G-XWBC",
        "level": "warning",
        "url": "https://fa.example/ABC",
        "photo_url": "https://img.example/1.jpg",
    }


# -------- helper: _channel_accepts + _instantiate --------


def test_channel_accepts_gates_disabled_channels():
    assert _channel_accepts(_telegram(enabled=False), "watchlist") is False


def test_channel_accepts_honours_category_flags():
    assert _channel_accepts(_telegram(watchlist_enabled=False), "watchlist") is False
    assert _channel_accepts(_telegram(emergency_enabled=False), "emergency") is False
    # Other category still goes through.
    assert _channel_accepts(_telegram(emergency_enabled=False), "watchlist") is True


def test_instantiate_returns_none_for_unready_channels():
    got = _instantiate({"type": "telegram", "bot_token": "x", "chat_id": ""}, lambda: None)
    assert got is None


def test_instantiate_builds_the_right_type():
    assert isinstance(_instantiate(_telegram(), lambda: None), TelegramNotifier)
    assert isinstance(_instantiate(_ntfy(), lambda: None), NtfyNotifier)
    assert isinstance(_instantiate(_webhook(), lambda: None), WebhookNotifier)


# -------- Dispatcher --------


def test_dispatcher_disabled_when_config_is_empty(tmp_path: Path):
    d = NotifierDispatcher(_store(tmp_path))
    assert d.enabled is False
    # Dispatch with no channels is a silent no-op.
    n = asyncio.run(d.dispatch("t", "b", category="watchlist"))
    assert n == 0


def test_dispatcher_ignores_unready_channels(tmp_path: Path):
    # Channel saved but missing chat_id — doesn't count towards `enabled`.
    d = NotifierDispatcher(_store(tmp_path, [_telegram(chat_id="")]))
    assert d.enabled is False


def test_dispatcher_reports_configured_summary(tmp_path: Path):
    d = NotifierDispatcher(_store(tmp_path, [_telegram(), _telegram(name="Group"), _ntfy()]))
    assert d.configured_summary() == ["ntfyx1", "telegramx2"]


def test_dispatcher_fans_out_in_parallel(tmp_path: Path, monkeypatch):
    """Every configured + category-matching channel gets the alert; one
    channel's failure doesn't suppress the others."""
    d = NotifierDispatcher(_store(tmp_path, [_telegram(), _ntfy(), _webhook()]))
    sent: list[str] = []

    async def fake_send_ok(self, title, body, **kw):
        sent.append(type(self).__name__)

    async def fake_send_boom(self, title, body, **kw):
        sent.append(type(self).__name__)
        raise RuntimeError("nope")

    monkeypatch.setattr(TelegramNotifier, "send", fake_send_boom)
    monkeypatch.setattr(NtfyNotifier, "send", fake_send_ok)
    monkeypatch.setattr(WebhookNotifier, "send", fake_send_ok)
    n = asyncio.run(d.dispatch("t", "b", category="watchlist"))
    assert n == 3
    assert set(sent) == {"TelegramNotifier", "NtfyNotifier", "WebhookNotifier"}


def test_dispatcher_filters_by_category(tmp_path: Path, monkeypatch):
    """Emergency-only channel must not receive watchlist alerts."""
    d = NotifierDispatcher(
        _store(
            tmp_path,
            [
                _telegram(name="Everything"),
                _telegram(name="Emergency only", watchlist_enabled=False),
            ],
        )
    )
    sent: list[str] = []

    async def fake_send(self, title, body, **kw):
        sent.append(body)

    monkeypatch.setattr(TelegramNotifier, "send", fake_send)

    # Watchlist alert: the "emergency only" channel is excluded.
    n = asyncio.run(d.dispatch("t", "watchlist-body", category="watchlist"))
    assert n == 1
    # Emergency alert: both channels accept.
    n2 = asyncio.run(d.dispatch("t", "emergency-body", category="emergency"))
    assert n2 == 2


def test_dispatcher_skips_disabled_channels(tmp_path: Path, monkeypatch):
    d = NotifierDispatcher(_store(tmp_path, [_telegram(enabled=False), _ntfy()]))
    sent = []
    monkeypatch.setattr(
        NtfyNotifier,
        "send",
        lambda self, *a, **kw: sent.append("ntfy") or _noop(),
    )
    asyncio.run(d.dispatch("t", "b", category="watchlist"))
    assert sent == ["ntfy"]


async def _noop():
    return None


def test_dispatcher_test_channel_delegates_to_send(tmp_path: Path, monkeypatch):
    store = _store(tmp_path, [_telegram(id="abc12345")])
    d = NotifierDispatcher(store)
    captured: list[tuple[str, str]] = []

    async def fake_send(self, title, body, **kw):
        captured.append((title, body))

    monkeypatch.setattr(TelegramNotifier, "send", fake_send)
    ok = asyncio.run(d.test_channel("abc12345"))
    assert ok is True
    assert captured and "test" in captured[0][0].lower()


def test_dispatcher_test_channel_returns_false_for_unknown_id(tmp_path: Path):
    d = NotifierDispatcher(_store(tmp_path, [_telegram()]))
    ok = asyncio.run(d.test_channel("not-a-real-id"))
    assert ok is False


def test_dispatcher_aclose_releases_shared_client(tmp_path: Path):
    d = NotifierDispatcher(_store(tmp_path, [_telegram()]))
    _ = d._client()
    assert d._http is not None
    asyncio.run(d.aclose())
    assert d._http is None
