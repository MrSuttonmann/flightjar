"""Server-side notification fan-out driven by the UI-managed config.

Channel instances are lightweight wrappers around per-dispatch data
dicts, so we rebuild the active set from `NotificationsConfigStore` on
every call — no need for a reload signal when the user edits the
config via the HTTP endpoint.

Per-alert category filtering lives here rather than in AlertWatcher:
each channel config carries `watchlist_enabled` / `emergency_enabled`
bools that decide whether a given alert should fan out to that
channel. Keeps the watcher oblivious to channel plumbing.
"""

from __future__ import annotations

import asyncio
import logging
from typing import Any, ClassVar

import httpx

from .notifications_config import NotificationsConfigStore, channel_is_ready

log = logging.getLogger("beast.notifications")


# MarkdownV2 reserved set per https://core.telegram.org/bots/api#markdownv2-style
_TG_ESCAPE_CHARS = set(r"_*[]()~`>#+-=|{}.!\\")


def _tg_escape(text: str) -> str:
    """Escape every MarkdownV2 reserved char in `text`."""
    return "".join(f"\\{c}" if c in _TG_ESCAPE_CHARS else c for c in text)


class _Notifier:
    """Abstract base — subclasses override `send`."""

    def __init__(self, client_getter) -> None:
        self._client_getter = client_getter

    async def send(
        self,
        title: str,
        body: str,
        *,
        level: str = "info",
        url: str | None = None,
        photo_url: str | None = None,
    ) -> None:
        raise NotImplementedError


class TelegramNotifier(_Notifier):
    """Post to Telegram via the Bot API.

    `sendMessage` when there's no photo, `sendPhoto` (with text as
    caption) when there is — so watched-aircraft notifications embed
    the planespotters thumbnail inline.
    """

    def __init__(self, bot_token: str, chat_id: str, client_getter) -> None:
        super().__init__(client_getter)
        self._bot_token = bot_token
        self._chat_id = chat_id

    async def send(
        self,
        title: str,
        body: str,
        *,
        level: str = "info",
        url: str | None = None,
        photo_url: str | None = None,
    ) -> None:
        text = f"*{_tg_escape(title)}*\n{_tg_escape(body)}"
        if url:
            text += f"\n[Details]({url})"
        client = self._client_getter()
        payload: dict[str, Any]
        if photo_url:
            api = f"https://api.telegram.org/bot{self._bot_token}/sendPhoto"
            payload = {
                "chat_id": self._chat_id,
                "photo": photo_url,
                "caption": text,
                "parse_mode": "MarkdownV2",
            }
        else:
            api = f"https://api.telegram.org/bot{self._bot_token}/sendMessage"
            payload = {
                "chat_id": self._chat_id,
                "text": text,
                "parse_mode": "MarkdownV2",
                "disable_web_page_preview": True,
            }
        try:
            r = await client.post(api, json=payload)
            r.raise_for_status()
        except Exception as e:
            log.warning("telegram send failed: %s", e)


class NtfyNotifier(_Notifier):
    """Post to an ntfy topic (ntfy.sh or self-hosted)."""

    _PRIORITY: ClassVar[dict[str, str]] = {
        "emergency": "urgent",
        "warning": "high",
        "info": "default",
    }
    _LEVEL_TAG: ClassVar[dict[str, str]] = {
        "emergency": "rotating_light",
        "warning": "warning",
        "info": "airplane",
    }

    def __init__(self, url: str, token: str | None, client_getter) -> None:
        super().__init__(client_getter)
        self._url = url.rstrip("/") if url else ""
        self._token = token or None

    async def send(
        self,
        title: str,
        body: str,
        *,
        level: str = "info",
        url: str | None = None,
        photo_url: str | None = None,
    ) -> None:
        headers: dict[str, str] = {
            "Title": title,
            "Priority": self._PRIORITY.get(level, "default"),
            "Tags": self._LEVEL_TAG.get(level, "airplane"),
        }
        if url:
            headers["Click"] = url
        if photo_url:
            headers["Attach"] = photo_url
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        try:
            r = await self._client_getter().post(
                self._url,
                content=body.encode("utf-8"),
                headers=headers,
            )
            r.raise_for_status()
        except Exception as e:
            log.warning("ntfy send failed: %s", e)


class WebhookNotifier(_Notifier):
    """POST the alert as JSON to a user-configured URL.

    Schema:

        {"title": str, "body": str, "level": "info"|"warning"|"emergency",
         "url": str | None, "photo_url": str | None}
    """

    def __init__(self, url: str, client_getter) -> None:
        super().__init__(client_getter)
        self._url = url or ""

    async def send(
        self,
        title: str,
        body: str,
        *,
        level: str = "info",
        url: str | None = None,
        photo_url: str | None = None,
    ) -> None:
        payload = {
            "title": title,
            "body": body,
            "level": level,
            "url": url,
            "photo_url": photo_url,
        }
        try:
            r = await self._client_getter().post(self._url, json=payload)
            r.raise_for_status()
        except Exception as e:
            log.warning("webhook send failed: %s", e)


def _instantiate(channel: dict, client_getter) -> _Notifier | None:
    """Build the right notifier for a channel dict, or None if the
    channel isn't configured (yet)."""
    if not channel_is_ready(channel):
        return None
    ctype = channel.get("type")
    if ctype == "telegram":
        return TelegramNotifier(
            channel.get("bot_token", ""),
            channel.get("chat_id", ""),
            client_getter,
        )
    if ctype == "ntfy":
        return NtfyNotifier(
            channel.get("url", ""),
            channel.get("token") or None,
            client_getter,
        )
    if ctype == "webhook":
        return WebhookNotifier(channel.get("url", ""), client_getter)
    return None


def _channel_accepts(channel: dict, category: str) -> bool:
    """Gate: does this channel opt in to alerts of this category?"""
    if not channel.get("enabled", True):
        return False
    if category == "watchlist":
        return bool(channel.get("watchlist_enabled", True))
    if category == "emergency":
        return bool(channel.get("emergency_enabled", True))
    return True


class NotifierDispatcher:
    """Fans alerts out to enabled channels in parallel. Channels are
    re-read from the config store on every dispatch so UI edits take
    effect immediately without a reload signal."""

    _DEFAULT_TIMEOUT = 15.0

    def __init__(self, config_store: NotificationsConfigStore) -> None:
        self._config = config_store
        self._http: httpx.AsyncClient | None = None

    def _client(self) -> httpx.AsyncClient:
        if self._http is None:
            self._http = httpx.AsyncClient(timeout=self._DEFAULT_TIMEOUT)
        return self._http

    @property
    def enabled(self) -> bool:
        """True when *any* channel is configured + enabled. Used by
        AlertWatcher as a cheap gate to skip per-aircraft work when
        nothing could fire."""
        return any(c.get("enabled", True) and channel_is_ready(c) for c in self._config.channels())

    def configured_summary(self) -> list[str]:
        """Human-friendly list like ['telegramx2', 'webhookx1'] — used
        for a startup info log so the operator can confirm the channel
        list got loaded."""
        counts: dict[str, int] = {}
        for c in self._config.channels():
            if c.get("enabled", True) and channel_is_ready(c):
                counts[c.get("type", "?")] = counts.get(c.get("type", "?"), 0) + 1
        return [f"{t}x{n}" for t, n in sorted(counts.items())]

    async def dispatch(
        self,
        title: str,
        body: str,
        *,
        category: str,
        level: str = "info",
        url: str | None = None,
        photo_url: str | None = None,
    ) -> int:
        """Fan out `title` / `body` to every channel that (a) is enabled,
        (b) is configured, and (c) opts in to this `category`. Returns
        the number of channels that received the send call (regardless
        of whether the upstream API succeeded — each notifier logs its
        own failures)."""
        channels = [
            c
            for c in self._config.channels()
            if c.get("enabled", True) and _channel_accepts(c, category)
        ]
        notifiers: list[_Notifier] = []
        for c in channels:
            n = _instantiate(c, self._client)
            if n is not None:
                notifiers.append(n)
        if not notifiers:
            return 0
        await asyncio.gather(
            *(n.send(title, body, level=level, url=url, photo_url=photo_url) for n in notifiers),
            return_exceptions=True,
        )
        return len(notifiers)

    async def test_channel(self, channel_id: str) -> bool:
        """Send a one-off test alert through a single configured
        channel. Used by the UI's Test button so users can verify a
        token / URL without waiting for a live event. Returns True when
        the channel was found + configured (the HTTP call may still
        silently fail; we're verifying plumbing, not uptime)."""
        channel = self._config.find(channel_id)
        if channel is None:
            return False
        notifier = _instantiate(channel, self._client)
        if notifier is None:
            return False
        await notifier.send(
            "Flightjar test alert",
            f"Test message from the {channel.get('type', '?')} channel"
            f" '{channel.get('name', '')}'. If you can see this, it's wired up.",
            level="info",
        )
        return True

    async def aclose(self) -> None:
        if self._http is not None:
            await self._http.aclose()
            self._http = None
