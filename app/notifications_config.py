"""Persistent, UI-managed notification channel configuration.

Three channel types are supported: telegram, ntfy, and webhook. Users
can configure multiple of each (e.g. a Telegram chat for your phone
+ another for a group, two webhooks bridging to Home Assistant and
Slack), each with its own name and per-category enable flags so a
channel can opt in to emergency-only alerts without getting watchlist
pings.

Schema (JSON on disk):

    {
      "version": 1,
      "channels": [
        {
          "id": "uuid",
          "type": "telegram" | "ntfy" | "webhook",
          "name": "Phone",
          "enabled": true,
          "watchlist_enabled": true,
          "emergency_enabled": true,
          ... type-specific fields ...
        }
      ]
    }

Type-specific fields:

- telegram: `bot_token`, `chat_id`
- ntfy:     `url`, `token` (optional)
- webhook:  `url`

Persistence is atomic (write-to-temp + rename) so a crash mid-save
can't leave a half-written file.
"""

from __future__ import annotations

import json
import logging
import os
import uuid
from pathlib import Path
from typing import Any

log = logging.getLogger("beast.notifications_config")

_SCHEMA_VERSION = 1

CHANNEL_TYPES = ("telegram", "ntfy", "webhook")

# Fields each channel type requires to be considered "configured" —
# missing any of these when enabled means the channel is silently
# dropped at dispatch time. UI validation mirrors this.
_REQUIRED_FIELDS: dict[str, tuple[str, ...]] = {
    "telegram": ("bot_token", "chat_id"),
    "ntfy": ("url",),
    "webhook": ("url",),
}

# Fields we allow through per channel type (everything else is
# stripped). Keeps the on-disk shape tight and stops a
# maliciously-crafted POST from injecting arbitrary keys.
_ALLOWED_FIELDS: dict[str, tuple[str, ...]] = {
    "telegram": ("bot_token", "chat_id"),
    "ntfy": ("url", "token"),
    "webhook": ("url",),
}


def _str_or_empty(v: Any) -> str:
    if v is None:
        return ""
    if isinstance(v, str):
        return v.strip()
    return ""


def _bool(v: Any, default: bool = True) -> bool:
    if isinstance(v, bool):
        return v
    return default


def _clean_channel(raw: Any) -> dict[str, Any] | None:
    """Normalise + validate one raw channel entry. Returns the cleaned
    dict ready to persist, or None if the entry is malformed."""
    if not isinstance(raw, dict):
        return None
    ctype = raw.get("type")
    if ctype not in CHANNEL_TYPES:
        return None

    cleaned: dict[str, Any] = {
        "id": _str_or_empty(raw.get("id")) or uuid.uuid4().hex[:12],
        "type": ctype,
        "name": _str_or_empty(raw.get("name")) or f"{ctype} channel",
        "enabled": _bool(raw.get("enabled"), True),
        "watchlist_enabled": _bool(raw.get("watchlist_enabled"), True),
        "emergency_enabled": _bool(raw.get("emergency_enabled"), True),
    }
    for field in _ALLOWED_FIELDS[ctype]:
        cleaned[field] = _str_or_empty(raw.get(field))
    return cleaned


def channel_is_ready(channel: dict) -> bool:
    """True when the channel has all required fields set. Driven by the
    per-type `_REQUIRED_FIELDS` map; a Telegram channel missing `chat_id`
    can sit in the config (e.g. mid-edit) without accidentally firing."""
    ctype = channel.get("type")
    if ctype not in _REQUIRED_FIELDS:
        return False
    return all(channel.get(f) for f in _REQUIRED_FIELDS[ctype])


class NotificationsConfigStore:
    """In-memory + atomic on-disk notifications config."""

    def __init__(self, path: Path | None = None) -> None:
        self.path = path
        self._channels: list[dict[str, Any]] = []
        self._load()

    # -------- public API --------

    def get(self) -> dict[str, Any]:
        """Return the full config payload — the shape the /api endpoint
        sends and accepts. Channels are deep-copied so callers can't
        accidentally mutate our state."""
        return {
            "version": _SCHEMA_VERSION,
            "channels": [dict(c) for c in self._channels],
        }

    def channels(self) -> list[dict[str, Any]]:
        """Return a fresh list of channel dicts, deep-copied."""
        return [dict(c) for c in self._channels]

    def find(self, channel_id: str) -> dict[str, Any] | None:
        """Look up a channel by id; returns a deep copy."""
        for c in self._channels:
            if c.get("id") == channel_id:
                return dict(c)
        return None

    def replace(self, raw: Any) -> dict[str, Any]:
        """Replace the full channel list from a client POST body.

        Accepts either `{"channels": [...]}` or a bare list. Drops any
        entry that fails validation. Persists only when the cleaned
        config differs from the current state.
        """
        incoming = raw.get("channels") if isinstance(raw, dict) else raw
        if not isinstance(incoming, list):
            incoming = []
        cleaned: list[dict[str, Any]] = []
        seen_ids: set[str] = set()
        for entry in incoming:
            c = _clean_channel(entry)
            if c is None:
                continue
            # Avoid duplicate IDs from a malformed client; regenerate
            # when one slips through.
            if c["id"] in seen_ids:
                c["id"] = uuid.uuid4().hex[:12]
            seen_ids.add(c["id"])
            cleaned.append(c)
        if cleaned != self._channels:
            self._channels = cleaned
            self._persist()
        return self.get()

    # -------- persistence --------

    def _load(self) -> None:
        if self.path is None or not self.path.exists():
            return
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
        except Exception as e:
            log.warning("notifications config unreadable at %s: %s", self.path, e)
            return
        if not isinstance(data, dict):
            return
        raw_channels = data.get("channels") or []
        if not isinstance(raw_channels, list):
            return
        self._channels = [c for c in (_clean_channel(r) for r in raw_channels) if c is not None]
        log.info("loaded %d notification channel(s) from %s", len(self._channels), self.path)

    def _persist(self) -> None:
        if self.path is None:
            return
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            tmp = self.path.with_suffix(self.path.suffix + ".tmp")
            tmp.write_text(
                json.dumps(
                    {"version": _SCHEMA_VERSION, "channels": self._channels},
                    separators=(",", ":"),
                ),
                encoding="utf-8",
            )
            os.replace(tmp, self.path)
        except Exception as e:
            log.warning("couldn't persist notifications config: %s", e)
