"""Download tar1090's markers.js and massage it into a bundled ES module.

tar1090 defines per-type aircraft silhouettes as SVG polygon paths in a
`shapes` object, with ICAO type codes mapped to shape names + scales in
`TypeDesignatorIcons`. We grab the raw source, turn its top-level data
declarations into ES exports, and ship the result unchanged otherwise.

Also fetches tar1090-db's `icao_aircraft_types.json` — the lookup that
turns an ICAO type designator (e.g. "C172") into its ICAO type
description ("L1P") + wake turbulence category ("L"). The frontend uses
these to drive tar1090's full fallback chain when a specific designator
isn't in `TypeDesignatorIcons`.

Sources:
- https://github.com/wiedehopf/tar1090 (GPL-2.0+, compatible with FlightJar's GPL-3.0)
- https://github.com/wiedehopf/tar1090-db (CC0)

Override the URLs via TAR1090_MARKERS_URL / TAR1090_TYPES_URL if needed.
"""

import json
import os
import sys
import urllib.request

URL = os.environ.get(
    "TAR1090_MARKERS_URL",
    "https://raw.githubusercontent.com/wiedehopf/tar1090/master/html/markers.js",
)
DEST = os.environ.get(
    "TAR1090_SHAPES_DEST",
    "/app/app/static/tar1090_shapes.js",
)
TYPES_URL = os.environ.get(
    "TAR1090_TYPES_URL",
    "https://raw.githubusercontent.com/wiedehopf/tar1090-db/master/icao_aircraft_types.json",
)
TYPES_DEST = os.environ.get(
    "TAR1090_TYPES_DEST",
    "/app/app/static/icao_aircraft_types.js",
)
# Top-level bindings we want to surface as ES module exports. Everything
# else in markers.js (helper functions, sprite code, iconTest) is left
# untouched — function declarations don't run on import, so they're
# harmless even if they reference globals we don't have.
EXPORTS = (
    "shapes",
    "TypeDesignatorIcons",
    "TypeDescriptionIcons",
    "CategoryIcons",
)


def main() -> int:
    print(f"fetching {URL}", file=sys.stderr)
    with urllib.request.urlopen(URL, timeout=60) as r:
        src = r.read().decode("utf-8")

    # Everything below `getBaseMarker` is tar1090's sprite-test UI (references
    # `usp`, `jQuery`, DOM globals we don't have). Drop it — we only need the
    # data tables above.
    cut = src.find("function getBaseMarker")
    if cut < 0:
        raise SystemExit("couldn't find function getBaseMarker in markers.js")
    data = src[:cut].rstrip() + "\n"

    header = (
        "// Auto-generated at build time by scripts/fetch_plane_shapes.py.\n"
        "// Source: tar1090 / markers.js (GPL-2.0+).\n"
        "// https://github.com/wiedehopf/tar1090\n"
    )
    for name in EXPORTS:
        # replace(..., count=1) so only the top-level declaration is
        # touched; tar1090 never redeclares these names.
        data = data.replace(f"let {name} = ", f"export const {name} = ", 1)

    os.makedirs(os.path.dirname(DEST), exist_ok=True)
    with open(DEST, "w", encoding="utf-8") as f:
        f.write(header + data)
    print(f"wrote {DEST} ({len(data)} bytes)", file=sys.stderr)

    write_types_module()
    return 0


def write_types_module() -> None:
    print(f"fetching {TYPES_URL}", file=sys.stderr)
    with urllib.request.urlopen(TYPES_URL, timeout=60) as r:
        types_raw = r.read().decode("utf-8")
    types = json.loads(types_raw)

    # Drop empty entries to keep the bundle lean. The source has a small
    # number of {} placeholders; rendering needs at least one of desc/wtc.
    compact = {
        designator: {k: v for k, v in entry.items() if k in ("desc", "wtc") and v}
        for designator, entry in sorted(types.items())
        if any(entry.get(k) for k in ("desc", "wtc"))
    }
    body = "export const TypeDesignatorMeta = " + json.dumps(
        compact, separators=(",", ":"), ensure_ascii=False
    ) + ";\n"
    header = (
        "// Auto-generated at build time by scripts/fetch_plane_shapes.py.\n"
        "// Source: tar1090-db / icao_aircraft_types.json (CC0).\n"
        "// https://github.com/wiedehopf/tar1090-db\n"
        "// Maps ICAO type designator -> { desc: 'L2J', wtc: 'M' }, where\n"
        "// `desc` is the ICAO type description and `wtc` is wake turbulence.\n"
    )
    os.makedirs(os.path.dirname(TYPES_DEST), exist_ok=True)
    with open(TYPES_DEST, "w", encoding="utf-8") as f:
        f.write(header + body)
    print(f"wrote {TYPES_DEST} ({len(body)} bytes, {len(compact)} entries)", file=sys.stderr)


if __name__ == "__main__":
    sys.exit(main())
