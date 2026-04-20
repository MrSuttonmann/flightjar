# ---- builder ----
#
# Install the runtime deps into a self-contained virtualenv and fetch the
# external data artefacts that get baked into the image (aircraft DB,
# airports DB, tar1090 silhouettes). Keeping all of this in the builder
# means pip, its wheel cache, and the silhouette-fetch script never land
# in the runtime image.
FROM python:3.12-slim AS builder

# --copies so the venv is self-contained — the runtime stage can just
# lift /opt/venv across without worrying about dangling symlinks.
RUN python -m venv --copies /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

WORKDIR /build
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# The three fetches below pull data that changes upstream (aircraft DB
# refreshed daily by tar1090-db, OurAirports updated continuously,
# tar1090 markers.js on every tar1090 release). Docker would otherwise
# cache them by RUN string and freeze us on whatever snapshot happened
# to be live the first time a given host built the image. CI sets
# DATA_CACHEBUST per commit (see .github/workflows/ci.yml) so the
# fetches always re-run; local builds can override with
#   docker build --build-arg DATA_CACHEBUST=$(date +%s) .
# or `--no-cache` if they want a guaranteed-fresh rebuild.
ARG DATA_CACHEBUST=static

ARG AIRCRAFT_DB_URL=https://raw.githubusercontent.com/wiedehopf/tar1090-db/refs/heads/csv/aircraft.csv.gz
RUN echo "data fetch ${DATA_CACHEBUST}" \
 && python -c "import urllib.request; urllib.request.urlretrieve('${AIRCRAFT_DB_URL}', '/build/aircraft_db.csv.gz')"

ARG AIRPORTS_DB_URL=https://raw.githubusercontent.com/davidmegginson/ourairports-data/main/airports.csv
RUN python -c "import urllib.request; urllib.request.urlretrieve('${AIRPORTS_DB_URL}', '/build/airports.csv')"

COPY app /build/app
COPY scripts/fetch_plane_shapes.py /build/scripts/fetch_plane_shapes.py
RUN TAR1090_SHAPES_DEST=/build/app/static/tar1090_shapes.js \
    python /build/scripts/fetch_plane_shapes.py


# ---- runtime ----
#
# Same base image so the venv's compiled extensions line up. We copy the
# venv plus the app + its baked-in data assets; pip, gcc, and the fetch
# script stay behind.
FROM python:3.12-slim

ENV PATH="/opt/venv/bin:$PATH" \
    PYTHONUNBUFFERED=1 \
    BEAST_HOST=readsb \
    BEAST_PORT=30005 \
    BEAST_OUTFILE=/data/beast.jsonl \
    BEAST_ROTATE=daily \
    BEAST_ROTATE_KEEP=14 \
    BEAST_STDOUT=0 \
    BEAST_NO_DECODE=0 \
    SNAPSHOT_INTERVAL=1.0

WORKDIR /app

COPY --from=builder /opt/venv /opt/venv
COPY --from=builder /build/app /app/app
COPY --from=builder /build/aircraft_db.csv.gz /app/app/aircraft_db.csv.gz
COPY --from=builder /build/airports.csv /app/app/airports.csv

VOLUME ["/data"]
EXPOSE 8080

# /healthz returns 503 when the BEAST feed is disconnected, which makes
# urlopen() raise and the healthcheck fail — exactly what we want.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8080/healthz', timeout=3).read()"

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080", "--no-access-log"]
