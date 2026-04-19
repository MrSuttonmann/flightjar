"""Download tar1090's markers.js and massage it into a bundled ES module.

tar1090 defines per-type aircraft silhouettes as SVG polygon paths in a
`shapes` object, with ICAO type codes mapped to shape names + scales in
`TypeDesignatorIcons`. We grab the raw source, turn its top-level data
declarations into ES exports, and ship the result unchanged otherwise.

Source: https://github.com/wiedehopf/tar1090 (GPL-2.0+, compatible with
Flightjar's GPL-3.0). Override the URL via the TAR1090_MARKERS_URL env
var if needed.
"""

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
    return 0


if __name__ == "__main__":
    sys.exit(main())
