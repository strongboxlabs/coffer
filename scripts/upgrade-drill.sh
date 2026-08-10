#!/usr/bin/env bash
# =============================================================================
# upgrade-drill.sh — prove an EXISTING install survives being upgraded.
# =============================================================================
#
# WHY THIS EXISTS: v0.44.0 was tested hard — eight restore scenarios, a real
# production dump restored onto the new schema, unit tests across three
# environments — and the upgrade path was never run once. It failed on the first
# real host: install.sh moved the role passwords out of .env, THEN failed compose
# interpolation, leaving a live install half-migrated with `docker compose`
# unusable. Every test that existed passed, because they all started from a fresh
# install.
#
# New-code correctness and upgrade correctness are different properties. The
# second only shows up against a host that already has state — an old compose
# file, passwords in the old location, a schema at the previous version. This
# drill manufactures that host and upgrades it.
#
# WHAT IT ASSERTS, in the order the failures actually bite:
#   1. An old-style install (passwords in .env, pre-secrets compose) comes up.
#   2. Swapping in the CURRENT compose without secrets/ FAILS — and fails
#      cleanly, having changed nothing.
#   3. The migration (secrets/ written, .env commented) then makes it resolve.
#   4. The upgraded stack serves, and the roles authenticate from FILES.
#   5. .env is never left without a usable password source at any point.
#
# Assertion 5 is the regression that motivated the script: it is not enough that
# the end state is right, because the failure mode was a broken INTERMEDIATE
# state on a live host.
#
# Throwaway by construction: own compose project, own volumes, own port. Removes
# them on exit, pass or fail.
#
#   scripts/upgrade-drill.sh            # against the working tree's compose
#   KEEP=1 scripts/upgrade-drill.sh     # leave the stack up for inspection
# =============================================================================
set -uo pipefail

export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

PROJECT="coffer-upgrade-drill"
WORK="$(mktemp -d)"
API_PORT="${DRILL_API_PORT:-8099}"
PG_PORT="${DRILL_PG_PORT:-55099}"
failed=0

info() { printf '\n== %s\n' "$*"; }
ok()   { printf '   OK: %s\n' "$*"; }
bad()  { printf '   !! %s\n' "$*" >&2; failed=1; }

# Run compose from INSIDE the work directory with no path arguments. Absolute
# paths are a trap on Git Bash: bash writes to /tmp/tmp.X while docker.exe
# resolves the same string to D:\tmp\tmp.X, and the file "cannot be found" even
# though it is right there. A cd plus compose's own discovery of
# ./docker-compose.yml and ./.env avoids the translation entirely, and is
# identical behaviour on Linux.
dc() { ( cd "$WORK" && docker compose -p "$PROJECT" "$@" ); }

cleanup() {
    if [ "${KEEP:-0}" = 1 ]; then
        printf '\nKEEP=1 — stack left at %s (port %s). Remove with:\n  (cd %s && docker compose -p %s down -v)\n' \
            "$WORK" "$API_PORT" "$WORK" "$PROJECT"
        return
    fi
    dc down -v --remove-orphans >/dev/null 2>&1 || true
    rm -rf "$WORK"
}
trap cleanup EXIT

# --------------------------------------------------------------- the OLD install
# Reconstructed rather than checked out of git: what matters is the SHAPE an
# existing host has — role passwords as environment variables, a compose file
# that declares them required — not any specific historical revision. `:?` is the
# detail that made this bite: it is what turns a missing variable into a hard
# interpolation failure instead of an empty value.
info "Building an old-style install (passwords in .env, pre-secrets compose)"
mkdir -p "$WORK/db/init"
cp "$repo_root/db/init/00-init-roles.sh" "$WORK/db/init/"

cat >"$WORK/docker-compose.yml" <<'OLD'
services:
  postgres:
    image: postgres:16
    environment:
      POSTGRES_USER: ${POSTGRES_USER:-coffer}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-changeme}
      POSTGRES_DB: ${POSTGRES_DB:-coffer}
      COFFER_SERVICE_PASSWORD: ${COFFER_SERVICE_PASSWORD:?COFFER_SERVICE_PASSWORD must be set in .env}
      COFFER_APP_PASSWORD:     ${COFFER_APP_PASSWORD:?COFFER_APP_PASSWORD must be set in .env}
    ports: ["127.0.0.1:${POSTGRES_PORT:-5432}:5432"]
    volumes:
      - postgres_data:/var/lib/postgresql/data
      - ./db/init:/docker-entrypoint-initdb.d:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U $${POSTGRES_USER}"]
      interval: 5s
      timeout: 5s
      retries: 20
volumes:
  postgres_data:
OLD

cat >"$WORK/.env" <<ENV
POSTGRES_USER=coffer
POSTGRES_DB=coffer
POSTGRES_PORT=$PG_PORT
POSTGRES_PASSWORD=drill_super_pw
COFFER_SERVICE_PASSWORD=drill_service_pw
COFFER_APP_PASSWORD=drill_app_pw
API_PORT=$API_PORT
# Required by the current compose (no default — see docker-compose.yml). The drill
# never pulls or starts the api service, so the value only has to interpolate.
COFFER_IMAGE=ghcr.io/drill/coffer
ENV

# postgres only: the api service has a build context this throwaway directory
# doesn't have, and the topology under test is the database's credentials.
dc up -d postgres >/dev/null 2>&1 || { bad "the old-style stack would not start"; exit 1; }
for _ in $(seq 1 60); do
    dc exec -T postgres pg_isready -U coffer >/dev/null 2>&1 && break
    sleep 2
done
if dc exec -T -e PGPASSWORD=drill_service_pw postgres \
      psql -w -h 127.0.0.1 -U coffer_service -d coffer -tAc 'select 1' >/dev/null 2>&1; then
    ok "old-style install is up and its roles authenticate from .env"
else
    bad "old-style install never became usable — drill cannot proceed"; exit 1
fi

# ------------------------------------- 2. the hazard: .env stripped, old compose
# THE regression. On a real host the passwords were moved out of .env while the
# compose file still in place declared them required, and interpolation died with
# the install half-migrated. Reproduce that state deliberately and confirm it is
# as fatal as it was, so the guard below is protecting against something real
# rather than something imagined.
info "Hazard check: strip .env passwords while the OLD compose still requires them"
cp -a "$WORK/.env" "$WORK/.env.hazard-backup"
sed -E -i 's@^(POSTGRES_PASSWORD|COFFER_SERVICE_PASSWORD|COFFER_APP_PASSWORD)=@# moved: \1=@' "$WORK/.env"

if dc config -q >/dev/null 2>&1; then
    bad "old compose resolved without its passwords — the failure mode is not being reproduced"
else
    ok "confirmed fatal: this is the half-migrated state install.sh must never create"
fi
mv "$WORK/.env.hazard-backup" "$WORK/.env"

# --------------------------------------- 3. the supported order: secrets, then .env
# Writing secrets/ is additive — the old compose ignores those files entirely, so
# a host interrupted here is unchanged. Only once the NEW compose is in place and
# resolving may .env be stripped.
info "Migrating in the supported order (secrets/ first, .env last)"
cp "$repo_root/docker-compose.yml" "$WORK/docker-compose.yml"

# container_name is the one thing a compose project name does NOT namespace — it
# is global to the daemon. The real compose file hardcodes coffer-postgres, which
# on a developer's machine is already taken by their dev stack; Docker then
# creates the container under a mangled <hash>_coffer-postgres name that compose
# itself can no longer find, and every later `exec` silently targets nothing.
# Override to drill-specific names so this is isolated from whatever else is up.
cat >"$WORK/docker-compose.override.yml" <<'OVERRIDE'
services:
  postgres:
    container_name: coffer-drill-postgres
  api:
    container_name: coffer-drill-api
OVERRIDE

# Ordering guard: if writing secrets/ fails, .env must be left ALONE. This is the
# invariant the real incident violated — the passwords were taken away before
# their replacement was known-good. Forced by pointing SECRETS_DIR at a path whose
# parent is a FILE, so mkdir cannot succeed; no permission bits involved, so it
# behaves the same on Windows, Linux, and as root.
printf 'not a directory\n' >"$WORK/blocker"
env_before="$(sha256sum "$WORK/.env" | cut -d' ' -f1)"
if COFFER_DIR="$WORK" SECRETS_DIR="$WORK/blocker/secrets" \
     bash "$repo_root/scripts/migrate-db-secrets.sh" >/dev/null 2>&1; then
    bad "migration reported success although it could not write secrets/"
elif [ "$(sha256sum "$WORK/.env" | cut -d' ' -f1)" = "$env_before" ]; then
    ok "a failed secrets/ write leaves .env intact (passwords never taken away first)"
else
    bad ".env was modified even though secrets/ could not be written — the original bug"
fi
rm -f "$WORK/blocker"

COFFER_DIR="$WORK" bash "$repo_root/scripts/migrate-db-secrets.sh" >/dev/null 2>&1 \
    || bad "migrate-db-secrets.sh exited non-zero"

for f in postgres_password coffer_service_password coffer_app_password; do
    [ -s "$WORK/secrets/$f" ] || bad "secrets/$f missing or empty"
done
[ -s "$WORK/secrets/coffer_app_password" ] \
    && [ "$(cat "$WORK/secrets/coffer_app_password")" = "drill_app_pw" ] \
    && ok "passwords carried across unchanged (migration must not rotate)"

if grep -qE '^(POSTGRES|COFFER_SERVICE|COFFER_APP)_PASSWORD=' "$WORK/.env"; then
    bad ".env still has live password lines after migration"
else
    ok ".env password lines commented out"
fi

if dc config -q >/dev/null 2>&1; then
    ok "compose resolves once secrets/ exists"
else
    bad "compose still does not resolve after the migration"
fi

# --------------------------------------------------------- 4. the upgraded stack
info "Bringing the upgraded stack up"
dc up -d postgres >/dev/null 2>&1
for _ in $(seq 1 60); do
    dc exec -T postgres pg_isready -U coffer >/dev/null 2>&1 && break
    sleep 2
done
if dc exec -T -e PGPASSWORD=drill_app_pw postgres \
      psql -w -h 127.0.0.1 -U coffer_app -d coffer -tAc 'select 1' >/dev/null 2>&1; then
    ok "coffer_app still authenticates after the upgrade"
else
    bad "coffer_app cannot authenticate after the upgrade"
fi

if dc config 2>/dev/null | grep -q 'POSTGRES_PASSWORD_FILE'; then
    ok "postgres reads its password from a file"
else
    bad "postgres is not using the *_FILE form"
fi

if dc config 2>/dev/null | grep -qE '^\s+POSTGRES_PASSWORD:'; then
    bad "a password is still being passed as an environment variable"
else
    ok "no role password passed through the environment"
fi

echo ""
if [ "$failed" -eq 0 ]; then
    echo "PASS: an existing install upgrades without a broken intermediate state."
else
    echo "FAIL: see !! lines above." >&2
fi
exit "$failed"
