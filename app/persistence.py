"""Persistence for AircraftRegistry state.

Writes a gzipped JSON snapshot atomically (write to temp, then rename) so
readers never see half-written files. Load on startup restores aircraft and
their trails so the UI has history to show immediately.
"""

import gzip
import json
import logging
import os
from pathlib import Path
from typing import TYPE_CHECKING

if TYPE_CHECKING:  # avoid runtime import cycle
    from .aircraft import AircraftRegistry

log = logging.getLogger("beast.persistence")


def save_state(registry: "AircraftRegistry", path: Path) -> None:
    """Serialise the registry and write it atomically to `path`."""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    data = registry.serialize()
    with gzip.open(tmp, "wt", encoding="utf-8") as fh:
        json.dump(data, fh, separators=(",", ":"))
    os.replace(tmp, path)


def load_state(registry: "AircraftRegistry", path: Path) -> int:
    """Restore registry state from `path`. Returns the number of aircraft loaded."""
    if not path.exists():
        return 0
    try:
        with gzip.open(path, "rt", encoding="utf-8") as fh:
            data = json.load(fh)
    except Exception as e:
        log.warning("couldn't read persisted state at %s: %s", path, e)
        return 0
    try:
        n = registry.restore(data)
    except Exception as e:
        log.warning("couldn't restore state: %s", e)
        return 0
    log.info("restored %d aircraft from %s", n, path)
    return n
