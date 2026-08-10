#!/usr/bin/env bash
# Regression guard for the whole-DB restore path (ADR-0060/0061).
#
# The bootstrap-UI / admin restore must succeed when applied OVER an already
# populated database (the normal case: the API's own DbUp run has already built
# a schema before the restore fires, and the backup may even be from a DIFFERENT
# schema version). The original implementation used `pg_restore --clean
# --if-exists`, which is fatally fragile there: the archive's DROP list only
# covers objects that existed at the backup's version, cannot CASCADE, and
# cannot drop the superuser-owned extensions — so dependents block the drops and
# the CREATE/COPY phase then collides, leaving a half-applied hybrid.
#
# The fix (BackupService.WipeServiceOwnedObjectsAsync) wipes the schema to empty
# first — dropping ONLY what the non-superuser service role owns, with CASCADE,
# leaving the superuser-owned extensions intact — then pg_restore into the clean
# schema with no --clean. This script reproduces the exact ownership topology
# that broke --clean (app objects owned by a non-superuser role; extensions
# owned by a superuser) and asserts:
#
#   1. the OLD `--clean --if-exists` restore over the populated DB FAILS, and
#   2. the NEW wipe-then-restore SUCCEEDS with the data intact and the
#      extensions preserved.
#
# The wipe SQL below is a faithful copy of BackupService.WipeSchemaSql; if you
# change one, change the other. Runs against the dev Postgres container by
# default; override with PG_CONTAINER / SUPERUSER.
set -euo pipefail

# Git Bash (MSYS) rewrites in-container paths like /tmp/... into a Windows path
# before they reach `docker exec`; disable that so they pass through verbatim.
# No-op on Linux/CI.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

PG_CONTAINER="${PG_CONTAINER:-coffer-postgres}"
SUPERUSER="${SUPERUSER:-coffer}"          # owns the extensions (rolsuper)
TESTDB="coffer_roundtrip_test"
OWNER="rt_owner"                          # non-superuser; owns the app objects
OWNER_PW="rt_pw"
DUMP="/tmp/${TESTDB}.dump"

# Superuser password. These calls go over the container's unix socket, which
# used to be `trust` — initdb's default — so no password was needed. Fresh
# installs now set --auth-local=scram-sha-256 (see docker-compose.yml), so the
# socket demands a password like every other path and this has to be supplied.
# Installs created before that change still have trust, where a supplied
# password is simply ignored — so this works either way.
#
# The secret file is the source of truth (docker-compose `secrets:`); .env is
# the pre-migration fallback, where the value lived before it moved into a file.
# Override either with SUPERUSER_PW or SECRETS_DIR (the latter for a parallel
# test stack with its own credentials).
_repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SECRETS_DIR="${SECRETS_DIR:-$_repo_root/secrets}"
_repo_env="$_repo_root/.env"
if [ -z "${SUPERUSER_PW:-}" ]; then
    if [ -s "$SECRETS_DIR/postgres_password" ]; then
        SUPERUSER_PW="$(cat "$SECRETS_DIR/postgres_password")"
    else
        SUPERUSER_PW="$(grep -E '^POSTGRES_PASSWORD=' "$_repo_env" 2>/dev/null | head -1 | cut -d= -f2- || true)"
    fi
fi
[ -n "$SUPERUSER_PW" ] || {
    echo "backup-restore-roundtrip: no superuser password found." >&2
    echo "  looked in: $SECRETS_DIR/postgres_password, then POSTGRES_PASSWORD in $_repo_env" >&2
    echo "  override with SUPERUSER_PW=… or point SECRETS_DIR at the right directory." >&2
    exit 1
}

# Run psql as the superuser against an arbitrary db; -q quiet, stop on error.
su_psql() { docker exec -i -e PGPASSWORD="$SUPERUSER_PW" "$PG_CONTAINER" psql -v ON_ERROR_STOP=1 -q -U "$SUPERUSER" -d "$1"; }
# Run a client tool as the non-superuser owner (mirrors coffer_service).
owner_exec() { docker exec -i -e PGPASSWORD="$OWNER_PW" "$PG_CONTAINER" "$@"; }

cleanup() {
  docker exec -i -e PGPASSWORD="$SUPERUSER_PW" "$PG_CONTAINER" psql -q -U "$SUPERUSER" -d postgres \
    -c "DROP DATABASE IF EXISTS ${TESTDB} WITH (FORCE);" >/dev/null 2>&1 || true
  docker exec -i "$PG_CONTAINER" rm -f "$DUMP" >/dev/null 2>&1 || true
}
trap cleanup EXIT

# The wipe under test — MUST mirror BackupService.WipeSchemaSql.
WIPE_SQL=$(cat <<'SQL'
DO $wipe$
DECLARE cmd text;
BEGIN
    FOR cmd IN
        SELECT format('DROP TABLE IF EXISTS %I.%I CASCADE', schemaname, tablename)
        FROM pg_tables WHERE schemaname = 'public' AND tableowner = current_user
    LOOP EXECUTE cmd; END LOOP;
    FOR cmd IN
        SELECT format('DROP MATERIALIZED VIEW IF EXISTS %I.%I CASCADE', n.nspname, c.relname)
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND c.relkind = 'm'
          AND pg_get_userbyid(c.relowner) = current_user
    LOOP EXECUTE cmd; END LOOP;

    FOR cmd IN
        SELECT format('DROP VIEW IF EXISTS %I.%I CASCADE', schemaname, viewname)
        FROM pg_views WHERE schemaname = 'public' AND viewowner = current_user
    LOOP EXECUTE cmd; END LOOP;
    FOR cmd IN
        SELECT format('DROP SEQUENCE IF EXISTS %I.%I CASCADE', schemaname, sequencename)
        FROM pg_sequences WHERE schemaname = 'public' AND sequenceowner = current_user
    LOOP EXECUTE cmd; END LOOP;
    FOR cmd IN
        SELECT format('DROP FUNCTION IF EXISTS %s CASCADE', p.oid::regprocedure)
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        JOIN pg_roles r   ON r.oid = p.proowner
        WHERE n.nspname = 'public' AND r.rolname = current_user
          AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = p.oid AND d.deptype = 'e')
    LOOP EXECUTE cmd; END LOOP;

    -- Collations LAST: tables whose columns use them must go first.
    FOR cmd IN
        SELECT format('DROP COLLATION IF EXISTS %I.%I CASCADE', n.nspname, c.collname)
        FROM pg_collation c
        JOIN pg_namespace n ON n.oid = c.collnamespace
        WHERE n.nspname = 'public'
          AND pg_get_userbyid(c.collowner) = current_user
    LOOP EXECUTE cmd; END LOOP;
END
$wipe$;
SQL
)

echo "== setup: throwaway DB with superuser-owned extensions + non-superuser-owned app objects =="
cleanup
su_psql postgres <<SQL
CREATE DATABASE ${TESTDB};
DO \$r\$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='${OWNER}') THEN
    CREATE ROLE ${OWNER} LOGIN NOBYPASSRLS PASSWORD '${OWNER_PW}';
  END IF;
END \$r\$;
SQL

su_psql "$TESTDB" <<SQL
CREATE EXTENSION IF NOT EXISTS pgcrypto;   -- superuser-owned, like the real install
CREATE EXTENSION IF NOT EXISTS pg_trgm;
GRANT CREATE, USAGE ON SCHEMA public TO ${OWNER};
SQL

# App objects created + owned by the non-superuser role, with an inter-table FK
# (the dependency that blocks an ordering-blind DROP) plus a view, sequence, and
# a function that uses the superuser-owned extension.
owner_exec psql -v ON_ERROR_STOP=1 -q -U "$OWNER" -d "$TESTDB" <<SQL
-- A COLLATION, and a column that uses it. This is not decoration: the real
-- schema has one (username_ci), the wipe originally didn't drop collations, and
-- so every cross-install CLI restore failed on "collation already exists" while
-- this guard passed — because the synthetic schema had no collation to collide.
-- A guard that omits an object class the real schema uses is a guard with a
-- hole exactly the shape of the bug.
CREATE COLLATION name_ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
CREATE TABLE parent (id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                     name text COLLATE name_ci);
CREATE TABLE child  (id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                     parent_id uuid NOT NULL REFERENCES parent(id));
-- A MATERIALIZED VIEW: pg_views structurally excludes these, so the wipe needs
-- its own pg_class relkind='m' pass and nothing would reveal its absence until
-- the first migration added one.
CREATE MATERIALIZED VIEW parent_counts AS SELECT name, 1 AS n FROM parent;
CREATE VIEW parent_children AS
  SELECT p.name, count(c.id) AS n FROM parent p LEFT JOIN child c ON c.parent_id=p.id GROUP BY p.name;
CREATE SEQUENCE widget_seq;
CREATE FUNCTION hashit(t text) RETURNS text LANGUAGE sql AS \$\$ SELECT encode(digest(t,'sha256'),'hex') \$\$;
INSERT INTO parent(name) SELECT 'p'||g FROM generate_series(1,5) g;
INSERT INTO child(parent_id) SELECT id FROM parent, generate_series(1,3);
SQL

PARENTS=$(owner_exec psql -tAX -U "$OWNER" -d "$TESTDB" -c "SELECT count(*) FROM parent;")
CHILDREN=$(owner_exec psql -tAX -U "$OWNER" -d "$TESTDB" -c "SELECT count(*) FROM child;")
echo "   seeded: parent=$PARENTS child=$CHILDREN"

echo "== backup: pg_dump -Fc --no-owner =="
docker exec -i -e PGPASSWORD="$OWNER_PW" "$PG_CONTAINER" \
  pg_dump -Fc --no-owner -U "$OWNER" -d "$TESTDB" -f "$DUMP"

# Diverge the live schema: an extra table the backup doesn't know about (the
# cross-version analog) + extra rows. The target is now populated + divergent.
owner_exec psql -v ON_ERROR_STOP=1 -q -U "$OWNER" -d "$TESTDB" <<SQL
CREATE TABLE only_in_live (id int PRIMARY KEY);
INSERT INTO parent(name) VALUES ('STALE-ROW');
SQL

echo "== negative control: OLD 'pg_restore --clean --if-exists' over the populated DB =="
if owner_exec pg_restore --clean --if-exists --no-owner -U "$OWNER" -d "$TESTDB" "$DUMP" >/dev/null 2>&1; then
  echo "   !! UNEXPECTED: --clean restore succeeded — the failure it caused isn't being reproduced." >&2
  exit 1
else
  echo "   OK: --clean restore failed as expected (dependency/collision errors)."
fi

echo "== fix: wipe service-owned objects, then pg_restore with no --clean =="
owner_exec psql -v ON_ERROR_STOP=1 -q -U "$OWNER" -d "$TESTDB" -c "$WIPE_SQL"

# After the wipe: app objects gone, extensions preserved. Asserted PER OBJECT
# CLASS, because "no tables left" was true even while a collation survived and
# broke the restore — a count that only looks at pg_tables cannot see the class
# the wipe forgot.
LEFT=$(owner_exec psql -tAX -U "$OWNER" -d "$TESTDB" \
  -c "SELECT count(*) FROM pg_tables WHERE schemaname='public' AND tableowner='${OWNER}';")
LEFT_MV=$(owner_exec psql -tAX -U "$OWNER" -d "$TESTDB" -c \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
   WHERE n.nspname='public' AND c.relkind='m' AND pg_get_userbyid(c.relowner)='${OWNER}';")
LEFT_COLL=$(owner_exec psql -tAX -U "$OWNER" -d "$TESTDB" -c \
  "SELECT count(*) FROM pg_collation c JOIN pg_namespace n ON n.oid=c.collnamespace
   WHERE n.nspname='public' AND pg_get_userbyid(c.collowner)='${OWNER}';")
EXTS=$(su_psql "$TESTDB" <<<"SELECT count(*) FROM pg_extension WHERE extname IN ('pgcrypto','pg_trgm');" | tr -d '[:space:]')
[ "$LEFT" = "0" ]      || { echo "   !! wipe left $LEFT owned tables" >&2; exit 1; }
[ "$LEFT_MV" = "0" ]   || { echo "   !! wipe left $LEFT_MV owned materialized views" >&2; exit 1; }
[ "$LEFT_COLL" = "0" ] || { echo "   !! wipe left $LEFT_COLL owned collations — pg_restore will fail 'already exists'" >&2; exit 1; }
echo "   OK: schema emptied of tables, matviews and collations; pgcrypto/pg_trgm preserved."

# The only tolerated restore errors are the extension COMMENT/ownership ones
# (extensions are superuser-owned) — mirrors BackupService's benign filter. Any
# OTHER error line is a real failure.
set +e
RESTORE_ERR=$(owner_exec pg_restore --no-owner -U "$OWNER" -d "$TESTDB" "$DUMP" 2>&1 >/dev/null)
set -e
if printf '%s\n' "$RESTORE_ERR" | grep "pg_restore: error:" | grep -qv "must be owner of extension"; then
  echo "   !! non-benign restore error:" >&2; printf '%s\n' "$RESTORE_ERR" >&2; exit 1
fi

echo "== verify: data restored exactly, stale/divergent state gone =="
P2=$(owner_exec psql -tAX -U "$OWNER" -d "$TESTDB" -c "SELECT count(*) FROM parent;")
C2=$(owner_exec psql -tAX -U "$OWNER" -d "$TESTDB" -c "SELECT count(*) FROM child;")
STALE=$(owner_exec psql -tAX -U "$OWNER" -d "$TESTDB" -c "SELECT count(*) FROM parent WHERE name='STALE-ROW';")
LIVEONLY=$(owner_exec psql -tAX -U "$OWNER" -d "$TESTDB" -c "SELECT to_regclass('public.only_in_live') IS NOT NULL;")
HASH_OK=$(owner_exec psql -tAX -U "$OWNER" -d "$TESTDB" -c "SELECT length(hashit('x'))=64;")

fail=0
[ "$P2" = "$PARENTS" ]   || { echo "   !! parent count $P2 != $PARENTS" >&2; fail=1; }
[ "$C2" = "$CHILDREN" ]  || { echo "   !! child count $C2 != $CHILDREN" >&2; fail=1; }
[ "$STALE" = "0" ]       || { echo "   !! stale row survived" >&2; fail=1; }
[ "$LIVEONLY" = "f" ]    || { echo "   !! divergent 'only_in_live' table survived" >&2; fail=1; }
[ "$HASH_OK" = "t" ]     || { echo "   !! restored function can't reach pgcrypto" >&2; fail=1; }
[ "$fail" = "0" ] || exit 1

echo "   OK: parent=$P2 child=$C2, stale gone, divergent table gone, extension fn works."
echo
echo "PASS: restore over a populated DB is clean and deterministic."
