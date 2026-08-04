#!/usr/bin/env bash
# Bring up the full Coffer stack in Docker: Postgres + the
# single-container API+SPA on :8080 (ADR-0059).
#
# The container path, complementary to the native dev-up:
#   * dev-up (native)   — dotnet + Vite natively. Hot reload (Vite HMR, fast
#                         rebuilds), but no pg_dump on the host, so backups
#                         (ADR-0060) can't run.
#   * dev-up-docker.sh  — the real deployment artifact. Has
#                         postgresql-client-16, so backup create/restore work;
#                         exact prod parity. Trade-off: it's a production build,
#                         so code changes need a rebuild (just re-run this;
#                         layers are cached).
#
# docker compose reads .env for ${VAR} substitution, so there's no env plumbing
# here — just .env. Idempotent: re-run after a code change to rebuild + restart.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
echo "[dev-up-docker] repo: $repo_root"

# .env is required — docker compose substitutes ${COFFER_MASTER_KEK_BASE64}, the
# role passwords, etc. from it.
if [ ! -f "$repo_root/.env" ]; then
    echo "[dev-up-docker] .env not found — copy .env.example and fill it in." >&2
    exit 1
fi

# Stop native dev so two API instances don't run against the same DB. Kill the
# native apphost (coffer-api) and only node bound to the Vite ports — not every
# node on the box. Windows (Git Bash) uses taskkill/netstat; POSIX uses pkill.
if command -v taskkill >/dev/null 2>&1; then
    taskkill //F //IM coffer-api.exe >/dev/null 2>&1 || true
    if command -v netstat >/dev/null 2>&1; then
        for vp in 5173 5174 5175 5176; do
            for pid in $(netstat -ano 2>/dev/null | grep -E 'LISTENING' | grep -E "[:.]${vp}[[:space:]]" | awk '{print $NF}' | sort -u); do
                if tasklist //FI "PID eq ${pid}" 2>/dev/null | grep -qi 'node.exe'; then
                    echo "[dev-up-docker] stopping native Vite (node PID ${pid})"
                    taskkill //F //PID "${pid}" >/dev/null 2>&1 || true
                fi
            done
        done
    fi
else
    pkill -f 'coffer-api' 2>/dev/null || true
fi

# Tag the locally-built image with the CANONICAL source version, not whatever
# COFFER_IMAGE_TAG happens to sit in .env. In dev we always `docker compose build`
# from source, and compose names the built image `coffer:${COFFER_IMAGE_TAG}` — so a
# stale pinned tag in .env (COFFER_IMAGE_TAG is a manually-set pull tag for prod
# hosts, easy to forget on a release) would mislabel a fresh build with an old
# version. Deriving it from Api.csproj <Version> — the same value the API reports at
# /api/meta/version — makes the image label always match the code. An exported shell
# var wins over .env in compose substitution, so this overrides the pinned value for
# the build without touching .env (whose value still drives pull-based prod hosts).
source_version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo_root/src/Api/Api.csproj" | head -1 | tr -d '[:space:]')"
if [ -n "$source_version" ]; then
    export COFFER_IMAGE_TAG="$source_version"
    echo "[dev-up-docker] image tag ← Api.csproj <Version> = $source_version"
else
    echo "[dev-up-docker] WARNING: could not read <Version> from Api.csproj; using COFFER_IMAGE_TAG from .env" >&2
fi

# Build + (re)start the stack. --build picks up code changes since last run;
# --pull refreshes the base images (the floating dotnet 10.0 tags) so a rebuild
# gets the latest patched runtime instead of a stale cached layer — cheap when
# the base is unchanged (a registry digest check, no re-download). Postgres is
# reused if already healthy (data volume persists).
echo "[dev-up-docker] docker compose build --pull + up -d (image build ~30-90s) …"
docker compose build --pull
docker compose up -d

# Reap build cruft this rebuild left behind: the superseded image layers, and
# build cache older than a week (recent layers stay, so the next rebuild is still
# fast). ~13 GB of stale cache observed after weeks of rebuilds. No data risk —
# neither touches volumes or running containers; guarded so cleanup never fails up.
docker image prune -f >/dev/null 2>&1 || true
docker builder prune -f --filter until=168h >/dev/null 2>&1 || true

# API port: compose defaults to 8080 (override via API_PORT in .env).
port=8080
api_port_line="$(grep -E '^[[:space:]]*API_PORT[[:space:]]*=' "$repo_root/.env" | head -1 || true)"
if [ -n "$api_port_line" ]; then
    port="$(printf '%s' "${api_port_line#*=}" | tr -d '[:space:]')"
fi

# Wait for the API to answer. On a populated DB the container still runs DbUp at
# startup (no-op when already migrated), so give it room. /readyz is the
# anonymous readiness probe (process up AND Postgres reachable — ADR-0044); it
# 200s once the API is serving. (Not /api/meta/version, which is authenticated.)
ready=0
for _ in $(seq 1 180); do
    if curl -fsS -o /dev/null --max-time 2 "http://localhost:${port}/readyz" 2>/dev/null; then
        ready=1
        break
    fi
    sleep 1
done

if [ "$ready" -ne 1 ]; then
    echo "[dev-up-docker] API did not answer on :${port} within 3 min. Recent logs:" >&2
    docker compose logs --tail 40 api
    exit 1
fi

# First-run setup URL (ADR-0088). On a fresh DB the API logs a one-shot
# /setup/<token> link and never logs it again, so surface it here rather than
# making you dig through `docker compose logs`. Silent on an install that
# already has a user — bootstrap-token refuses once setup is done, and its
# stderr would just be noise on every subsequent dev-up.
# NB: `dotnet coffer-api.dll`, not `coffer-api` — the image's ENTRYPOINT is
# ["dotnet","coffer-api.dll"] and there is no apphost binary on PATH inside the
# container. (The retired provision.sh used `exec api coffer-api bootstrap-token`,
# which always exited 127; it was masked because its output was never checked.)
# The subcommand also emits DbUp log lines on stdout, hence the grep for the URL.
setup_url="$(docker compose exec -T api dotnet coffer-api.dll bootstrap-token 2>/dev/null \
    | grep -oE 'https?://[^[:space:]]+/setup/[^[:space:]]+' | head -1 || true)"

echo ""
echo "[dev-up-docker] ready — http://localhost:${port}"
if [ -n "$setup_url" ]; then
    echo ""
    echo "  First run — open this to create the first user (one-shot):"
    echo "    ${setup_url}"
    echo ""
fi
echo "  Logs:  docker compose logs -f api"
echo "  Stop:  docker compose down        (keeps the DB volume)"
echo "  Wipe:  docker compose down -v     (drops the DB volume too)"
