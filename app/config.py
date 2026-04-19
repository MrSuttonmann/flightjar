"""Typed configuration loaded from environment variables.

Validates input at startup so bad env vars produce a clear error rather than
crashing later in an unexpected spot (or, worse, silently falling through).
"""

from collections.abc import Mapping
from dataclasses import dataclass
from typing import Literal

RotateMode = Literal["none", "hourly", "daily"]
_ROTATE_VALID = ("none", "hourly", "daily")


class ConfigError(ValueError):
    """Raised when an env var can't be parsed or is outside its allowed range."""


def _env_str(env: Mapping[str, str], name: str, default: str = "") -> str:
    return env.get(name, default).strip()


def _env_int(env: Mapping[str, str], name: str, default: int) -> int:
    raw = _env_str(env, name)
    if not raw:
        return default
    try:
        return int(raw)
    except ValueError as e:
        raise ConfigError(f"{name}={raw!r}: not an integer") from e


def _env_float_required(env: Mapping[str, str], name: str, default: float) -> float:
    raw = _env_str(env, name)
    if not raw:
        return default
    try:
        return float(raw)
    except ValueError as e:
        raise ConfigError(f"{name}={raw!r}: not a number") from e


def env_bool(env: Mapping[str, str], name: str, default: str = "0") -> bool:
    return env.get(name, default).strip().lower() in ("1", "true", "yes", "on")


def env_float_optional(env: Mapping[str, str], name: str) -> float | None:
    """Parse an optional float env var. Returns None for empty/malformed."""
    raw = _env_str(env, name)
    if not raw:
        return None
    try:
        return float(raw)
    except ValueError:
        # Optional fields are lenient: log and fall back to None rather than crash.
        # (The caller can log — we don't depend on logging here to keep this pure.)
        return None


@dataclass(frozen=True)
class Config:
    beast_host: str = "readsb"
    beast_port: int = 30005

    lat_ref: float | None = None
    lon_ref: float | None = None
    receiver_anon_km: float = 0.0
    site_name: str | None = None

    jsonl_path: str = "/data/beast.jsonl"
    jsonl_rotate: RotateMode = "daily"
    jsonl_keep: int = 14
    jsonl_stdout: bool = False
    jsonl_decode: bool = True

    snapshot_interval: float = 1.0

    @classmethod
    def from_env(cls, env: Mapping[str, str] | None = None) -> "Config":
        import os

        env = env if env is not None else os.environ

        rotate = _env_str(env, "BEAST_ROTATE", "daily")
        if rotate not in _ROTATE_VALID:
            raise ConfigError(f"BEAST_ROTATE={rotate!r}: must be one of {_ROTATE_VALID}")

        port = _env_int(env, "BEAST_PORT", 30005)
        if not (1 <= port <= 65535):
            raise ConfigError(f"BEAST_PORT={port}: must be in 1..65535")

        keep = _env_int(env, "BEAST_ROTATE_KEEP", 14)
        if keep < 0:
            raise ConfigError(f"BEAST_ROTATE_KEEP={keep}: must be >= 0")

        interval = _env_float_required(env, "SNAPSHOT_INTERVAL", 1.0)
        if interval <= 0:
            raise ConfigError(f"SNAPSHOT_INTERVAL={interval}: must be > 0")

        site = _env_str(env, "SITE_NAME") or None
        jsonl_path = _env_str(env, "BEAST_OUTFILE", "/data/beast.jsonl")

        return cls(
            beast_host=_env_str(env, "BEAST_HOST", "readsb") or "readsb",
            beast_port=port,
            lat_ref=env_float_optional(env, "LAT_REF"),
            lon_ref=env_float_optional(env, "LON_REF"),
            receiver_anon_km=env_float_optional(env, "RECEIVER_ANON_KM") or 0.0,
            site_name=site,
            jsonl_path=jsonl_path,
            jsonl_rotate=rotate,  # type: ignore[arg-type]
            jsonl_keep=keep,
            jsonl_stdout=env_bool(env, "BEAST_STDOUT", "0"),
            jsonl_decode=not env_bool(env, "BEAST_NO_DECODE", "0"),
            snapshot_interval=interval,
        )
