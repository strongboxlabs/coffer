#!/usr/bin/env bash
# One-time pg_hba hardening for an ALREADY-INITIALIZED install.
#
# Fresh installs get this from docker-compose.yml's POSTGRES_INITDB_ARGS
# (--auth-local/--auth-host=scram-sha-256). But initdb runs exactly once, on the
# first boot of an empty data directory, so an install created before that landed
# keeps initdb's defaults in its PGDATA:
#
#   local all all           trust
#   host  all all 127.0.0.1 trust
#   host  all all ::1/128   trust
#
# Those grant superuser with NO credential to anything that can reach the
# container's socket or its own loopback. Not reachable from outside the
# container, so this is defense in depth rather than an open door — but it is a
# standing bypass of the RLS boundary the authorization model rests on, it fails
# in the wrong direction if the deployment ever moves to network_mode: host or a
# bare VM, and it is the kind of thing a reader of a public repo should not have
# to reason about.
#
# pg_hba is re-read on reload, so this needs no restart and no downtime.
#
# Idempotent: re-running once hardened is a no-op, so it is safe to call from an
# upgrade path.
#
# Usage:
#   scripts/harden-pg-hba.sh                      # acts on coffer-postgres
#   PG_CONTAINER=coffer-g-postgres ENV_FILE=g.env scripts/harden-pg-hba.sh
#
# Bails out BEFORE changing anything unless it can prove the superuser password
# actually works, because that password becomes the only way in.
set -euo pipefail

# Git Bash (MSYS) rewrites in-container paths like /var/lib/... into Windows
# paths before they reach docker exec; disable that. No-op on Linux.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PG_CONTAINER="${PG_CONTAINER:-coffer-postgres}"
ENV_FILE="${ENV_FILE:-.env}"
env_path="$repo_root/$ENV_FILE"
HBA=/var/lib/postgresql/data/pg_hba.conf
BACKUP="$HBA.pre-harden"

die() { echo "[harden-pg-hba] ERROR: $*" >&2; exit 1; }
info() { echo "[harden-pg-hba] $*"; }

read_env() { grep -E "^$1=" "$env_path" 2>/dev/null | head -1 | cut -d= -f2- || true; }

# Credentials come from the secret files (docker-compose `secrets:`), falling
# back to .env for an install that predates the move. Reading only .env would
# break on any migrated install, where the password line is commented out.
SECRETS_DIR="${SECRETS_DIR:-$repo_root/secrets}"
read_credential() {
    local file="$1" var="$2"
    if [ -s "$SECRETS_DIR/$file" ]; then cat "$SECRETS_DIR/$file"; return 0; fi
    read_env "$var"
}

docker inspect "$PG_CONTAINER" >/dev/null 2>&1 || die "container '$PG_CONTAINER' not found."
[ -f "$env_path" ] || [ -d "$SECRETS_DIR" ] || die "neither $ENV_FILE nor $SECRETS_DIR found at $repo_root."

SUPERUSER="${POSTGRES_USER:-$(read_env POSTGRES_USER)}"; SUPERUSER="${SUPERUSER:-coffer}"
PGDB="${POSTGRES_DB:-$(read_env POSTGRES_DB)}"; PGDB="${PGDB:-coffer}"
SU_PW="$(read_credential postgres_password POSTGRES_PASSWORD)"
APP_PW="$(read_credential coffer_app_password COFFER_APP_PASSWORD)"
[ -n "$SU_PW" ] || die "no superuser password found (looked in $SECRETS_DIR/postgres_password, then POSTGRES_PASSWORD in $ENV_FILE)."

# Already done? Only non-comment lines count.
if ! docker exec "$PG_CONTAINER" grep -qE '^\s*(local|host)\s.*\strust\s*$' "$HBA"; then
    info "no trust rules present — already hardened, nothing to do."
    exit 0
fi

# The container's own bridge address. Connections to it match the `host all all
# all` rule, which is ALREADY scram — so authenticating there proves the password
# is right. Testing over 127.0.0.1 or the socket would prove nothing while those
# are still trust: any password, including a wrong one, succeeds under trust.
pg_ip="$(docker exec "$PG_CONTAINER" hostname -i | tr -d '\r' | awk '{print $1}')"
[ -n "$pg_ip" ] || die "could not determine the container's own IP."

info "verifying the superuser password over a scram path ($pg_ip) before changing anything…"
docker exec -e PGPASSWORD="$SU_PW" "$PG_CONTAINER" \
    psql -w -h "$pg_ip" -U "$SUPERUSER" -d "$PGDB" -tAc 'select 1' >/dev/null 2>&1 \
    || die "POSTGRES_PASSWORD in $ENV_FILE does not authenticate. Fix that first — after this change it is the only way in."

if [ -n "$APP_PW" ]; then
    docker exec -e PGPASSWORD="$APP_PW" "$PG_CONTAINER" \
        psql -w -h "$pg_ip" -U coffer_app -d "$PGDB" -tAc 'select 1' >/dev/null 2>&1 \
        || die "COFFER_APP_PASSWORD does not authenticate — the API would break. Fix that first."
    info "coffer_app authenticates too."
fi

info "current trust rules:"
docker exec "$PG_CONTAINER" grep -E '^\s*(local|host)\s.*\strust\s*$' "$HBA" | sed 's/^/    /'

# Keep a copy. It stays in PGDATA; it is inert (only the live file is consulted)
# and it is what the rollback below restores.
docker exec "$PG_CONTAINER" cp "$HBA" "$BACKUP"

rollback() {
    info "rolling back to $BACKUP…"
    docker exec "$PG_CONTAINER" cp "$BACKUP" "$HBA" || true
    docker exec -e PGPASSWORD="$SU_PW" "$PG_CONTAINER" \
        psql -w -h "$pg_ip" -U "$SUPERUSER" -d postgres -tAc 'select pg_reload_conf()' >/dev/null 2>&1 || true
}

# Swap the auth method on every trust rule. Anchored to end-of-line so it can
# only ever rewrite the METHOD column, never a database or role called "trust".
docker exec "$PG_CONTAINER" sed -i -E 's/^([[:space:]]*(local|host)[[:space:]].*[[:space:]])trust[[:space:]]*$/\1scram-sha-256/' "$HBA" \
    || { rollback; die "rewrite failed."; }

docker exec -e PGPASSWORD="$SU_PW" "$PG_CONTAINER" \
    psql -w -h "$pg_ip" -U "$SUPERUSER" -d postgres -tAc 'select pg_reload_conf()' >/dev/null \
    || { rollback; die "reload failed."; }

# Verify the new state: password required on the socket, and still working with
# one. A rollback on failure matters more than the failure message — a half-
# applied pg_hba is how you lock an app out of its own database.
if docker exec "$PG_CONTAINER" psql -w -U "$SUPERUSER" -d "$PGDB" -tAc 'select 1' >/dev/null 2>&1; then
    rollback; die "socket still accepts a password-less connection — rolled back."
fi
docker exec -e PGPASSWORD="$SU_PW" "$PG_CONTAINER" \
    psql -w -U "$SUPERUSER" -d "$PGDB" -tAc 'select 1' >/dev/null 2>&1 \
    || { rollback; die "socket rejects the correct password — rolled back."; }

if [ -n "$APP_PW" ]; then
    docker exec -e PGPASSWORD="$APP_PW" "$PG_CONTAINER" \
        psql -w -h "$pg_ip" -U coffer_app -d "$PGDB" -tAc 'select 1' >/dev/null 2>&1 \
        || { rollback; die "coffer_app can no longer connect — rolled back."; }
fi

info "done — every path requires a password now. Remaining rules:"
docker exec "$PG_CONTAINER" sh -c "grep -vE '^\s*#|^\s*\$' $HBA" | sed 's/^/    /'
info "previous file kept at $BACKUP inside the container (inert)."
