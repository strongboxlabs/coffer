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
# Required env vars (set in `.env`, surfaced via docker-compose):
#   COFFER_SERVICE_PASSWORD — password for coffer_service
#   COFFER_APP_PASSWORD     — password for coffer_app
#
# Both are required: the script fails with a clear message if either
# is unset, so a forgotten env var doesn't silently create roles with
# empty passwords.

set -euo pipefail

if [[ -z "${COFFER_SERVICE_PASSWORD:-}" ]]; then
    echo "00-init-roles.sh: COFFER_SERVICE_PASSWORD env var is not set." >&2
    echo "Set it in .env (see .env.example) before bringing up Postgres." >&2
    exit 1
fi
if [[ -z "${COFFER_APP_PASSWORD:-}" ]]; then
    echo "00-init-roles.sh: COFFER_APP_PASSWORD env var is not set." >&2
    echo "Set it in .env (see .env.example) before bringing up Postgres." >&2
    exit 1
fi

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
