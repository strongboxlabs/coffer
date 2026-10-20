#!/usr/bin/env bash
# Let the API restore a backup by building it as a NEW database and swapping it in
# (ADR-0061, amended 2026-10-09): coffer_service gets CREATEDB and becomes the owner
# of the live database. ALTER DATABASE ... RENAME requires both.
#
# Why the API needs this. A restore used to empty the live database object by
# object and reload it; the service role could do that without either privilege.
# It was also how restores broke: the emptying step worked from a hand-kept list of
# object kinds, two migrations added kinds the list did not know, and every restore
# over an existing install failed. Building a new database needs no such list and
# leaves the live one untouched until the new one is complete.
#
# What it changes, and why that is not a wider door. coffer_service already owns
# every table and can drop all of them; owning the database adds the ability to
# drop or rename the database as a whole, and CREATEDB the ability to create new
# ones (a disk-space consideration, not a data one). It gains no superuser power:
# the only extensions a restore creates, pgcrypto and pg_trgm, are trusted.
#
# Fresh installs get the same grants from db/init/00-init-roles.sh; this is for
# installs created before that. Idempotent — install.sh runs it on every upgrade,
# and running it twice changes nothing. Needs no restart.
#
#   sudo bash scripts/provision-restore-privileges.sh
set -euo pipefail

# Git Bash (MSYS) rewrites in-container paths before they reach docker exec;
# disable that. No-op on Linux.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# install.sh passes `sudo docker` on hosts where the user is not in the docker
# group. Unquoted on use, so a two-word value splits the way it must.
DOCKER="${DOCKER:-docker}"

die() { echo "[provision-restore-privileges] ERROR: $*" >&2; exit 1; }
info() { echo "[provision-restore-privileges] $*"; }

# Reach the database container through compose, from the install directory this
# script lives in — whatever COFFER_CONTAINER_PREFIX named it. PG_CONTAINER names
# a container directly instead (the restore drill's throwaway stack).
pg_exec() {
    if [ -n "${PG_CONTAINER:-}" ]; then
        $DOCKER exec -i "$PG_CONTAINER" "$@"
    else
        ( cd "$repo_root" && $DOCKER compose exec -T postgres "$@" )
    fi
}

# psql as the superuser, INSIDE the container: the password is read there from
# the container's own secret file (or, on an install that predates the move to
# files, its POSTGRES_PASSWORD), so it never appears on a host command line.
psql_su() {
    pg_exec sh -c 'export PGPASSWORD="${POSTGRES_PASSWORD:-$(cat "$POSTGRES_PASSWORD_FILE" 2>/dev/null)}"
        exec psql -w -X -q -At -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres -v db="$POSTGRES_DB"'
}

state() {
    psql_su <<'SQL'
SELECT r.rolcreatedb::text || '|' || (d.datdba = r.oid)::text
  FROM pg_roles r, pg_database d
 WHERE r.rolname = 'coffer_service' AND d.datname = :'db';
SQL
}

before="$(state)" || die "could not query the database as its superuser — is the stack up?"
[ -n "$before" ] || die "role coffer_service or the configured database not found."

if [ "$before" = "true|true" ]; then
    info "coffer_service already owns the database and may create databases — nothing to do."
    exit 0
fi

psql_su <<'SQL'
ALTER ROLE coffer_service CREATEDB;
ALTER DATABASE :"db" OWNER TO coffer_service;
SQL

after="$(state)"
[ "$after" = "true|true" ] || die "grants did not take (state: $after)."
info "done: coffer_service owns the database and may create databases. Restores can run."
