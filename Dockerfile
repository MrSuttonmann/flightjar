FROM python:3.12-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app /app/app

# Aircraft DB for ICAO24 -> registration / type lookup (tar1090-db / Mictronics).
# Users can override at runtime by placing a newer file at /data/aircraft_db.csv.gz.
ARG AIRCRAFT_DB_URL=https://github.com/wiedehopf/tar1090-db/raw/refs/heads/csv/aircraft.csv.gz
RUN python -c "import urllib.request; urllib.request.urlretrieve('${AIRCRAFT_DB_URL}', '/app/app/aircraft_db.csv.gz')"

# OurAirports CSV for ICAO airport code -> name / city lookup. Public domain.
# Override at runtime by placing /data/airports.csv.
ARG AIRPORTS_DB_URL=https://raw.githubusercontent.com/davidmegginson/ourairports-data/main/airports.csv
RUN python -c "import urllib.request; urllib.request.urlretrieve('${AIRPORTS_DB_URL}', '/app/app/airports.csv')"

# Tar1090 per-type aircraft silhouettes (GPL-2.0+) — SVG polygon paths
# exposed as an ES module for the frontend to import.
COPY scripts/fetch_plane_shapes.py /tmp/fetch_plane_shapes.py
RUN python /tmp/fetch_plane_shapes.py && rm /tmp/fetch_plane_shapes.py

VOLUME ["/data"]

ENV BEAST_HOST=readsb \
    BEAST_PORT=30005 \
    BEAST_OUTFILE=/data/beast.jsonl \
    BEAST_ROTATE=daily \
    BEAST_ROTATE_KEEP=14 \
    BEAST_STDOUT=0 \
    BEAST_NO_DECODE=0 \
    SNAPSHOT_INTERVAL=1.0 \
    PYTHONUNBUFFERED=1

EXPOSE 8080

# /healthz returns 503 when the BEAST feed is disconnected, which makes
# urlopen() raise and the healthcheck fail — exactly what we want.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8080/healthz', timeout=3).read()"

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080", "--no-access-log"]
