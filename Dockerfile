# syntax=docker/dockerfile:1
#
# Single-container Coffer image (ADR-0059): the API serves its own JSON under
# /api AND the built React SPA (same-origin, so cookies + Fido2 origins are
# trivial). Three stages: build the SPA, publish the API, assemble a slim
# runtime that carries the binary + db/migrations + wwwroot.

# MULTI-ARCH: the image publishes for linux/amd64 + linux/arm64 (a lot of
# self-hosters run Pi / Apple Silicon / Ampere). Emulating the whole build under QEMU
# would take 30-60+ min per release, because every heavy step here — npm ci, the Vite
# build, dotnet publish — happens INSIDE the image. So the two build stages are
# pinned to $BUILDPLATFORM and run natively, and only the final runtime stage is
# target-arch. The SPA bundle is architecture-independent JavaScript, and .NET
# cross-publishes with `-a $TARGETARCH`, so nothing is lost by building natively.

# --- Stage 1: build the SPA -------------------------------------------------
# --platform=$BUILDPLATFORM: JS output is arch-independent, so never emulate this.
FROM --platform=$BUILDPLATFORM node:22-alpine AS web
WORKDIR /web
# package files first so the npm layer caches across source-only changes.
COPY src/Web/package.json src/Web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/Web/ ./
# Vite git-stamps the build but guards a .git-less context (ADR-0044), so it
# builds fine here where .git is dockerignored.
RUN npm run build

# --- Stage 2: publish the API ----------------------------------------------
# Native SDK, cross-published for the target: the compiler runs at full speed and
# only the emitted apphost is arch-specific. TARGETARCH is supplied by buildx
# ('amd64' / 'arm64'), which is exactly what `dotnet publish -a` expects.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS api
ARG TARGETARCH
WORKDIR /src
COPY global.json ./
# Publish the API project only; restore pulls just its transitive project
# refs (Domain / Domain.Investment / Domain.Reminders), not the test or
# importer projects.
COPY src/ ./src/
# The API embeds the sample dataset for the setup form's "Include a Demo ledger"
# box (ADR-0088); data/ is otherwise dockerignored, with data/samples carved
# back in.
COPY data/samples ./data/samples
RUN dotnet publish src/Api/Api.csproj -c Release -a "$TARGETARCH" -o /publish

# --- Stage 3: runtime -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# pg_dump / pg_restore for whole-DB backup + restore (ADR-0060). Pin the major
# to match the postgres:16 server (PGDG repo) so pg_dump is never older than
# the server it dumps.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates gnupg \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(. /etc/os-release && echo $VERSION_CODENAME)-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-16 \
    && apt-get purge -y curl gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

COPY --from=api /publish ./
# The SPA bundle the API serves from wwwroot (UseStaticFiles + SPA fallback).
COPY --from=web /web/dist ./wwwroot
# DbUp reads these at startup; MigrationsDirectoryLocator walks up from the
# binary dir (/app) and finds /app/db/migrations.
COPY db/migrations ./db/migrations

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "coffer-api.dll"]
