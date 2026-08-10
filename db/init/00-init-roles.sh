#!/usr/bin/env bash
# Mints the two Coffer roles per ADR-0020 Phase D and PR 3.8:
#
#   * coffer_service — BYPASSRLS, used by the API's migration runner,
#     the importer, the WebAuthn ceremony handlers (pre-auth lookups),
#     and the future SimpleFIN sync worker. Sees every row across every
#     ledger.
#
#   * coffer_app — no BYPASSRLS, used by the API at request-handling
#     time. Every request sets `app.user_id` via SET on the pooled
#     connection; RLS policies on every ledger-scoped table filter
#     reads to that user's grants.
#
# Mounted into the Postgres container at
# `/docker-entrypoint-initdb.d/`. The Postgres image only runs the
# scripts in that directory once, against a fresh data directory; on
# subsequent container starts (existing volume) this script is a no-op
# from the daemon's perspective. The role creation itself is
# idempotent (CREATE if missing, ALTER if present) so a manual re-run
# is safe too.
#
# Each password comes from a FILE by preference, falling back to an
# environment variable:
#
#   COFFER_SERVICE_PASSWORD_FILE / COFFER_SERVICE_PASSWORD
#   COFFER_APP_PASSWORD_FILE     / COFFER_APP_PASSWORD
#
# The `*_FILE` form is the docker/compose secret convention the Postgres
# image itself uses (POSTGRES_PASSWORD_FILE), and it exists for the same
# reason ADR-0092 D1 moved the master KEK out of the environment: an env
# var is readable via `docker inspect`, /proc/<pid>/environ, any child
# process's environment and crash dumps.
#
# File wins when both are present. That direction is deliberate — during
# the transition an install has the password in both places, and if the
# env var won, moving the secret into a file would appear to work while
# changing nothing.
#
# One of the two is required per role: the script fails with a clear
# message if neither is set, so a forgotten variable doesn't silently
# create roles with empty passwords.

set -euo pipefail

# Resolve one password: $1 = role label, $2 = _FILE var name, $3 = plain
# var name. Echoes the password; exits non-zero with guidance if neither
# source yields one.
resolve_password() {
    local label="$1" file_var="$2" plain_var="$3"
    local path="${!file_var:-}" value="${!plain_var:-}"

    if [[ -n "$path" ]]; then
        if [[ ! -r "$path" ]]; then
            echo "00-init-roles.sh: $file_var points at '$path', which is not readable." >&2
            exit 1
        fi
        # Command substitution strips trailing newlines, which is exactly the
        # handling wanted: a file written by echo or an editor ends in one and
        # it is not part of the password. It leaves leading/trailing SPACES
        # alone, so a password that legitimately has them survives. This is
        # what the Postgres image's own *_FILE handling does.
        value="$(cat "$path")"
        if [[ -z "$value" ]]; then
            echo "00-init-roles.sh: the $label password file '$path' is empty." >&2
            exit 1
        fi
        printf '%s' "$value"
        return 0
    fi

    if [[ -z "$value" ]]; then
        echo "00-init-roles.sh: neither $file_var nor $plain_var is set." >&2
        echo "Point $file_var at a secret file (preferred — see docker-compose.yml)," >&2
        echo "or set $plain_var in .env (see .env.example)." >&2
        exit 1
    fi
    printf '%s' "$value"
}

COFFER_SERVICE_PASSWORD="$(resolve_password coffer_service COFFER_SERVICE_PASSWORD_FILE COFFER_SERVICE_PASSWORD)"
COFFER_APP_PASSWORD="$(resolve_password coffer_app COFFER_APP_PASSWORD_FILE COFFER_APP_PASSWORD)"

# Postgres dollar-quoted string literals (PG §4.1.2.4) are the
# cleanest way to inject a password that may contain single quotes or
# backslashes without escaping. The tag `$svc$` / `$app$` must not
# appear inside the password value itself — that's an enforceable
# constraint via the .env.example template (alphanumeric +
# punctuation excluding `$tag$` sequences).
psql -v ON_ERROR_STOP=1 \
     --username "${POSTGRES_USER}" \
     --dbname "${POSTGRES_DB}" \
     <<SQL
-- Install extensions up-front, as the superuser. CREATE EXTENSION
-- requires superuser unless the extension is marked trusted; pg_trgm
-- and pgcrypto aren't trusted by default, so the migration runner
-- (running as coffer_service, a non-superuser) can't install them.
-- Migration 001's CREATE EXTENSION IF NOT EXISTS becomes a no-op
-- once these are present.
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

DO \$Init\$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'coffer_service') THEN
        CREATE ROLE coffer_service LOGIN BYPASSRLS
            PASSWORD \$svc\$${COFFER_SERVICE_PASSWORD}\$svc\$;
    ELSE
        ALTER ROLE coffer_service WITH LOGIN BYPASSRLS
            PASSWORD \$svc\$${COFFER_SERVICE_PASSWORD}\$svc\$;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'coffer_app') THEN
        CREATE ROLE coffer_app LOGIN NOBYPASSRLS
            PASSWORD \$app\$${COFFER_APP_PASSWORD}\$app\$;
    ELSE
        ALTER ROLE coffer_app WITH LOGIN NOBYPASSRLS
            PASSWORD \$app\$${COFFER_APP_PASSWORD}\$app\$;
    END IF;
END \$Init\$;

-- Disable JIT for the runtime API role. DELIBERATE, measured-optimal
-- configuration for this OLTP workload — NOT an interim stopgap (see
-- ADR-0046 close-out + docs/follow-ups.md "View join cost").
--
-- The register read path goes through resolved_transactions, which
-- under ADR-0022 joins txn_headers + txn_legs + txn_header_overrides +
-- txn_leg_overrides + a self-LEFT-JOIN on txn_legs for the
-- counterparty, plus accounts and the recursive account_path()
-- function. A full-account / report scan over a large account crosses
-- jit_above_cost and Postgres compiles ~282 functions per query.
--
-- Re-measured on real data after the ADR-0046 work removed every
-- per-row correlated subquery (posting counts mig 120; txn_group_id
-- EXISTS mig 122) — on a ~16K-leg account, as coffer_app with RLS on:
--
--   Windowed page (LIMIT 50, the SPA's fetch):  ~102ms off / ~118ms on
--       — does NOT trip JIT either way (below jit_above_cost), so the
--         role setting is a no-op on the path users actually hit.
--   Full-account scan (reports/aggregation):    ~1653ms off / ~1755ms on
--       — STILL trips JIT (282 functions, ~70-100ms compile) with ZERO
--         execution benefit. JIT amortises compile over long compute-
--         bound execution; a row-returning OLTP read just pays the cost.
--
-- So jit=off is neutral on the page and a ~70-100ms win on heavy scans.
-- Lifting it would only REGRESS reports. coffer_service keeps JIT default
-- so genuinely compute-heavy batch jobs (importer, balance
-- reconciliation) can still benefit; per-statement SET LOCAL jit
-- overrides remain available if a future endpoint legitimately wants it.
ALTER ROLE coffer_app SET jit = off;

-- Schema-level grants. coffer_service needs CREATE so the DbUp
-- migration runner can create tables (and therefore own them, which
-- is the precondition for ALTER TABLE ... ENABLE ROW LEVEL SECURITY
-- in migration 017). coffer_app gets only USAGE here; the per-table
-- INSERT/SELECT/UPDATE/DELETE grants land in migration 017 once the
-- tables exist.
GRANT CREATE, USAGE ON SCHEMA public TO coffer_service;
GRANT USAGE          ON SCHEMA public TO coffer_app;
SQL

echo "00-init-roles.sh: coffer_service and coffer_app roles ensured."
