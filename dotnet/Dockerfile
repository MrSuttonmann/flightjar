# ---- builder ----
#
# Pinned to $BUILDPLATFORM so this entire stage runs once on the build
# host (typically linux/amd64) regardless of how many target platforms
# the runtime stage is being assembled for. Without this pin, every
# RUN here would execute under QEMU emulation for every non-native
# target arch — `dotnet publish` under emulated arm64 is 4–6× slower
# than native, and the architecture-agnostic data downloads (CSVs +
# the OpenFlights .dat) would re-fetch identical bytes per target.
#
# `dotnet publish` with no `-r` flag and `UseAppHost=false` produces
# portable IL — no native binaries, runs unchanged on any RID. The
# per-arch runtime stage just COPYs the same /out into both the
# linux/amd64 and linux/arm64 ASP.NET Core base images.
#
# The SDK image is chunky (~800 MB) so everything heavy lives here
# and the runtime image only gets the published output. Also fetches
# the public-domain data files (tar1090-db aircraft DB, OurAirports
# airports + navaids, OpenFlights airlines) + the tar1090 plane
# silhouettes bundle. `DATA_CACHEBUST` stuck above those RUN layers
# so Docker can't freeze us on a stale snapshot from an earlier build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS builder

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

# Telemetry destination, baked into the published assembly via
# AssemblyMetadata. Empty default = no destination = telemetry no-ops.
# CI sets this from a repo variable (PostHog phc_* project keys are
# public client-side keys, not secrets); local `docker build` can
# override with `--build-arg POSTHOG_API_KEY=phc_xxx`.
ARG POSTHOG_API_KEY=""
# UseAppHost=false skips the small native ./FlightJar.Api launcher —
# unused (ENTRYPOINT is `dotnet ...dll`) and the only build-host-arch-
# specific artifact `dotnet publish` would otherwise emit. Keeps /out
# fully portable across the per-arch runtime images downstream.
RUN dotnet publish src/FlightJar.Api/FlightJar.Api.csproj -c Release -o /out --no-restore \
    -p:PosthogApiKey="$POSTHOG_API_KEY" \
    -p:UseAppHost=false

# Upstream data — changes outside our control so we cache-bust on a
# rolling key. CI sets DATA_CACHEBUST to an ISO week (YYYY-WNN), so
# regular commits hit the cache and a real upstream change comes in
# within ~7 days. Local `docker build --build-arg DATA_CACHEBUST=$(date +%s)`
# forces a fresh pull; `--no-cache` is the sledgehammer fallback.
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
#
# Per-arch — this is the only stage buildx assembles separately for
# linux/amd64 and linux/arm64. No RUN under emulation needed (apt-get
# is a `RUN`, but this stage runs natively on each target node when
# the multi-arch manifest is being built; or, in single-arch builds,
# just once).
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
