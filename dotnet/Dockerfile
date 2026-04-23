# ---- builder ----
#
# Restores NuGet packages + publishes the Api in Release. The SDK image
# is chunky (~800 MB) so everything heavy lives here and the runtime
# image only gets the published output.
#
# Also fetches the public-domain data files (tar1090-db aircraft DB,
# OurAirports airports + navaids, OpenFlights airlines) + the tar1090
# plane silhouettes bundle. `DATA_CACHEBUST` stuck above those RUN layers
# so Docker can't freeze us on a stale snapshot from an earlier build.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder

# Install Python (small — ~20 MB) for the tar1090 shapes fetch script,
# and curl for the upstream data downloads.
RUN apt-get update && apt-get install -y --no-install-recommends python3 curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /build

# Copy csproj files first so the NuGet restore layer caches independently
# of source edits. Only the src projects (transitive deps of Api) are
# needed — tests never ship in the image and don't need restoring here.
COPY dotnet/global.json dotnet/Directory.Build.props ./
COPY dotnet/src/FlightJar.Api/FlightJar.Api.csproj src/FlightJar.Api/
COPY dotnet/src/FlightJar.Core/FlightJar.Core.csproj src/FlightJar.Core/
COPY dotnet/src/FlightJar.Decoder/FlightJar.Decoder.csproj src/FlightJar.Decoder/
COPY dotnet/src/FlightJar.Clients/FlightJar.Clients.csproj src/FlightJar.Clients/
COPY dotnet/src/FlightJar.Notifications/FlightJar.Notifications.csproj src/FlightJar.Notifications/
COPY dotnet/src/FlightJar.Persistence/FlightJar.Persistence.csproj src/FlightJar.Persistence/
COPY dotnet/src/FlightJar.Terrain/FlightJar.Terrain.csproj src/FlightJar.Terrain/
RUN dotnet restore src/FlightJar.Api/FlightJar.Api.csproj

COPY dotnet/src ./src
RUN dotnet publish src/FlightJar.Api/FlightJar.Api.csproj -c Release -o /out --no-restore

# Upstream data — changes outside our control so we cache-bust per build.
# CI sets DATA_CACHEBUST to the git sha; local `docker build --build-arg
# DATA_CACHEBUST=$(date +%s)` forces a fresh pull, `--no-cache` is the
# sledgehammer fallback.
ARG DATA_CACHEBUST=static

ARG AIRCRAFT_DB_URL=https://raw.githubusercontent.com/wiedehopf/tar1090-db/refs/heads/csv/aircraft.csv.gz
RUN echo "data fetch ${DATA_CACHEBUST}" \
 && curl -fsSL -o /out/aircraft_db.csv.gz "${AIRCRAFT_DB_URL}"

ARG AIRPORTS_DB_URL=https://raw.githubusercontent.com/davidmegginson/ourairports-data/main/airports.csv
RUN echo "data fetch ${DATA_CACHEBUST}" \
 && curl -fsSL -o /out/airports.csv "${AIRPORTS_DB_URL}"

ARG NAVAIDS_DB_URL=https://raw.githubusercontent.com/davidmegginson/ourairports-data/main/navaids.csv
RUN echo "data fetch ${DATA_CACHEBUST}" \
 && curl -fsSL -o /out/navaids.csv "${NAVAIDS_DB_URL}"

ARG AIRLINES_DB_URL=https://raw.githubusercontent.com/jpatokal/openflights/master/data/airlines.dat
RUN echo "data fetch ${DATA_CACHEBUST}" \
 && curl -fsSL -o /out/airlines.dat "${AIRLINES_DB_URL}"

# Copy the frontend static assets + run the tar1090 shapes fetcher. The
# Python app's app/static directory is shared verbatim with the .NET
# port; no duplication.
COPY app/static /out/static
COPY scripts/fetch_plane_shapes.py /tmp/fetch_plane_shapes.py
RUN echo "data fetch ${DATA_CACHEBUST}" \
 && TAR1090_SHAPES_DEST=/out/static/tar1090_shapes.js \
    python3 /tmp/fetch_plane_shapes.py

# esbuild is a standalone Go binary; we pull it straight from npm's
# registry (no Node runtime required) to minify the static JS + CSS at
# build time. Version-pinned for reproducible output. Runs AFTER the
# tar1090 shapes fetcher so the generated bundle is minified too.
# Tests + local `dotnet run` work against the unminified source tree,
# so this is strictly a runtime-image optimisation. IndexHtmlRenderer
# re-hashes the minified content at startup so browsers pick it up.
ARG ESBUILD_VERSION=0.24.0
RUN python3 -c "import platform, urllib.request, tarfile, shutil, os; \
arch={'x86_64':'x64','amd64':'x64','aarch64':'arm64','arm64':'arm64'}[platform.machine()]; \
url=f'https://registry.npmjs.org/@esbuild/linux-{arch}/-/linux-{arch}-${ESBUILD_VERSION}.tgz'; \
urllib.request.urlretrieve(url, '/tmp/esbuild.tgz'); \
tarfile.open('/tmp/esbuild.tgz').extractall('/tmp/esbuild'); \
shutil.move('/tmp/esbuild/package/bin/esbuild', '/usr/local/bin/esbuild'); \
os.chmod('/usr/local/bin/esbuild', 0o755)"
RUN set -eu; \
    for f in /out/static/*.js; do \
      esbuild --minify --target=es2020 --allow-overwrite --outfile="$f" "$f"; \
    done; \
    for f in /out/static/*.css; do \
      esbuild --minify --allow-overwrite --outfile="$f" "$f"; \
    done


# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl for the docker-native healthcheck.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

ARG FLIGHTJAR_VERSION=dev
ENV FLIGHTJAR_VERSION=${FLIGHTJAR_VERSION} \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    BEAST_HOST=readsb \
    BEAST_PORT=30005 \
    BEAST_OUTFILE=/data/beast.jsonl \
    BEAST_ROTATE=daily \
    BEAST_ROTATE_KEEP=14 \
    BEAST_STDOUT=0 \
    BEAST_NO_DECODE=0 \
    SNAPSHOT_INTERVAL=1.0

WORKDIR /app
COPY --from=builder /out ./

VOLUME ["/data"]
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz >/dev/null || exit 1

ENTRYPOINT ["dotnet", "FlightJar.Api.dll"]
