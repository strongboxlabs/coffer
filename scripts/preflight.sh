#!/usr/bin/env bash
# preflight.sh — parallel, CI-equivalent local gate for every .github/workflows/ci.yml job.
#
# WHY THIS EXISTS:
#   - CI usage has billing limits; pushing a PR that trips a job CI would have
#     caught locally (e.g. db/test/*.sql trigger verification) costs a run.
#   - This script mirrors every CI job, incl. spinning up a fresh ephemeral
#     Postgres and running the verify scripts.
#
# SPEED (2026-06-28): the .NET integration suite is the long pole (~9 min) because
# 85 of the Api.Tests classes share one xUnit collection (one DB) and run serially.
# We can't parallelize them IN-PROCESS — ApiFactory mutates process-global env vars
# (ASPNETCORE_ENVIRONMENT / COFFER_API__DevAuth / Mcp__Enabled / MasterKey__Path) that
# Program.cs reads eagerly at host build, and that's only safe while the collection
# runs sequentially. So instead we shard at the PROCESS level: build once, then run
# N `dotnet test --filter` processes in parallel, each with its own env-var space
# AND its own Testcontainers Postgres. The shard partition is by test namespace; the
# last shard is the COMPLEMENT (`!~` of all the others) so coverage is provably total
# — a newly added Integration/<Folder> can never be silently skipped. All the other
# jobs (web, schema, audit, doc) run in parallel alongside the shards. Net wall-clock
# drops from ~19 min (sequential) to ~the slowest single stage.
#
# Run before EVERY `git push`. If it doesn't end in "PREFLIGHT OK", do NOT push.
#
# SIDE EFFECTS:
#   - Stops a manually-started Vite (port :5173) + native coffer-api before the web
#     step so `npm ci` doesn't trip on locked native modules. The Docker dev stack
#     (scripts/dev-up-docker.sh) is unaffected — re-run it after the push.
#
# Usage:
#   bash scripts/preflight.sh              # all jobs — the push gate
#   bash scripts/preflight.sh --quick      # skip schema-apply (only safe when NO
#                                          # migration / db/test change is in the PR)
#   bash scripts/preflight.sh --only doc   # ONE stage, seconds not minutes
#   bash scripts/preflight.sh --only fast  # audit + doc
#   bash scripts/preflight.sh --only tests # every dotnet shard
#
# --only exists so iterating doesn't cost a full ~300s run: a docs-only edit needs
# the doc stage (~12s), and --only skips the Release build entirely when no shard is
# selected. It is for the EDIT LOOP, not for gating a push — a partial run prints
# "PARTIAL OK ... NOT a push gate" rather than the "PREFLIGHT OK" line, precisely so
# a green subset can't be mistaken for a verified tree. Aggregate your changes, then
# run the full gate once before pushing.

set -uo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

PG_IMAGE='postgres:17-alpine'
PG_CONTAINER='coffer-preflight-postgres'
# Must stay BELOW Windows' dynamic port range (49152-65535): WinNAT/Hyper-V
# reserve rolling blocks in there on each boot, and a reboot once swallowed the
# old 55433 into an excluded range (docker bind -> WSAEACCES). 15433 is well
# below the zone, so it survives reboots deterministically. (CI's schema job
# uses 15432 for the same reason — see .github/workflows/ci.yml.)
PG_PORT=15433
PG_USER='coffer'
PG_PASSWORD='preflight_pw'
PG_DB='coffer'

ALL_STAGES='schema audit identity web doc roundtrip test_s1 test_s1b test_s2 test_s3 test_s4 test_importer'

QUICK=0
ONLY=''      # empty = run every stage
while [[ $# -gt 0 ]]; do
    case "$1" in
        --quick) QUICK=1; shift ;;
        --only)
            [[ -n "${2-}" ]] || { echo "preflight: --only needs a stage list" >&2; exit 2; }
            ONLY=" ${2//,/ } "; shift 2 ;;
        --only=*)
            ONLY=" ${1#--only=}"; ONLY=" ${ONLY//,/ } "; shift ;;
        -h|--help)
            echo "usage: preflight.sh [--quick] [--only stage[,stage...]]"
            echo "  stages: $ALL_STAGES"
            echo "  groups: tests (every dotnet shard), fast (audit,doc)"
            exit 0 ;;
        *) echo "preflight: unknown option '$1'" >&2; exit 2 ;;
    esac
done

# Expand groups, then reject typos: silently running nothing and printing
# PREFLIGHT OK would be the worst possible failure mode for this script.
if [[ -n "$ONLY" ]]; then
    ONLY="${ONLY// tests / test_s1 test_s1b test_s2 test_s3 test_s4 test_importer }"
    ONLY="${ONLY// fast / audit doc }"
    for s in $ONLY; do
        [[ " $ALL_STAGES " == *" $s "* ]] \
            || { echo "preflight: unknown stage '$s' (have: $ALL_STAGES)" >&2; exit 2; }
    done
fi

# Should stage $1 run?
want() { [[ -z "$ONLY" || " $ONLY " == *" $1 "* ]]; }

logdir="$(mktemp -d)"
declare -A PID

cleanup() {
    docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
    rm -rf "$logdir" 2>/dev/null || true
    # Reap the anonymous data volumes the Testcontainers Postgres shards leave
    # behind (Ryuk reaps the containers, not their volumes) — hundreds of these
    # accumulate over time and slow Docker (17 GB observed). SAFE: the name filter
    # matches ONLY the 64-hex anonymous volume ids; the named compose volumes
    # (*_postgres_data / *_coffer_data) can never match, so dev data is untouchable.
    docker volume ls -q -f dangling=true 2>/dev/null \
        | grep -E '^[0-9a-f]{64}$' \
        | xargs -r docker volume rm >/dev/null 2>&1 || true
}
trap cleanup EXIT

# --------------------------------------------------------------
# Pre-req: a running dev API/Vite holds node_modules / bin open
# (lightningcss native binary; coffer-api.exe locks bin/). Stop them
# before the web preflight runs.
# --------------------------------------------------------------
stop_dev_processes() {
    taskkill //IM coffer-api.exe //F >/dev/null 2>&1 || true
    powershell -NoProfile -Command "Get-NetTCPConnection -LocalPort 5173 -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id \$_.OwningProcess -Force -ErrorAction SilentlyContinue }" >/dev/null 2>&1 || true
}
stop_dev_processes

# --------------------------------------------------------------
# Stage functions. Each writes all output to its own log (set up by
# `launch`) and returns non-zero on failure. They run concurrently.
# --------------------------------------------------------------

# Job 1: Schema apply + trigger verification (ephemeral Postgres).
stage_schema() {
    docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
    docker run -d --name "$PG_CONTAINER" \
        -p "127.0.0.1:${PG_PORT}:5432" \
        -e POSTGRES_USER="$PG_USER" \
        -e POSTGRES_PASSWORD="$PG_PASSWORD" \
        -e POSTGRES_DB="$PG_DB" \
        "$PG_IMAGE" >/dev/null || { echo "could not start postgres"; return 1; }

    # Readiness: poll a REAL query, not pg_isready. The official postgres image
    # starts a temporary server for init, then restarts the real one; pg_isready
    # (and even a single query) can succeed against the temp server, so a write
    # that lands in the restart window fails. Under the concurrent Docker load of
    # the parallel preflight that window is wider — it flaked role creation. We
    # gate on a real SELECT AND make provisioning idempotent + retried below.
    local ready=0 i
    for i in $(seq 1 90); do
        if docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -tAc 'SELECT 1' >/dev/null 2>&1; then
            ready=1; break
        fi
        sleep 1
    done
    [[ "$ready" -ne 1 ]] && { echo "Postgres never accepted a query within 90s."; return 1; }

    # Provision the roles CI seeds (mirrors db/init). coffer_service needs
    # BYPASSRLS for mig 017's RLS bootstrap; coffer_app is the runtime role.
    # Idempotent (IF NOT EXISTS) + retried so a connection that lands in the
    # init-restart window just tries again — no flake, no partial-state hazard.
    local provisioned=0
    for i in $(seq 1 15); do
        if docker exec -i "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -v ON_ERROR_STOP=1 >/dev/null 2>&1 <<SQL
DO \$\$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='coffer_service') THEN
    CREATE ROLE coffer_service LOGIN PASSWORD 'svc' SUPERUSER BYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='coffer_app') THEN
    CREATE ROLE coffer_app LOGIN PASSWORD 'app';
  END IF;
END \$\$;
GRANT ALL ON DATABASE $PG_DB TO coffer_service;
GRANT CONNECT ON DATABASE $PG_DB TO coffer_app;
GRANT USAGE ON SCHEMA public TO coffer_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO coffer_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO coffer_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO coffer_app;
SQL
        then provisioned=1; break; fi
        sleep 1
    done
    [[ "$provisioned" -ne 1 ]] && { echo "role provisioning failed after retries"; return 1; }

    local mig out
    for mig in db/migrations/*.sql; do
        out="$(docker exec -i "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -v ON_ERROR_STOP=1 < "$mig" 2>&1)" \
            || { echo "Migration failed: $mig"; echo "$out"; return 1; }
    done

    local test
    for test in db/test/*.sql; do
        out="$(docker exec -i "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -v ON_ERROR_STOP=1 < "$test" 2>&1)" \
            || { echo "Trigger verification failed: $test"; echo "$out"; return 1; }
        echo "  ok: $test"
    done
    return 0
}

# Job 2: API no-raw-sql audit.
stage_audit() {
    bash scripts/audit-no-raw-sql.sh
}

# Job 2b: maintainer-identity deny-list (see scripts/check-no-identity.sh for why
# this is automated rather than reviewed).
stage_identity() {
    bash scripts/check-no-identity.sh
}

# Job 3 (sharded): one slice of the .NET test suite, in its own process →
# its own env-var space + its own Testcontainers Postgres. Assumes the
# solution is already built (build-once below); runs with --no-build.
stage_dotnet_shard() {
    local label="$1" filter="$2"
    dotnet test tests/Api.Tests/Api.Tests.csproj \
        --configuration Release --no-build \
        --filter "$filter" \
        --results-directory "$logdir/trx-$label" \
        --logger "console;verbosity=minimal"
}

# Job 3b: the importer test project (fast; its own process).
stage_importer() {
    dotnet test tests/Importer.Moneydance.Tests/Importer.Moneydance.Tests.csproj \
        --configuration Release --no-build \
        --logger "console;verbosity=minimal"
}

# Job 4: Web typecheck + lint + test + build.
stage_web() {
    cd src/Web || return 1
    npm ci --no-audit --no-fund || { echo 'npm ci failed'; return 1; }
    npm run typecheck             || { echo 'typecheck failed'; return 1; }
    npm run lint                  || { echo 'lint failed'; return 1; }
    # Vitest summary can be misleading — scan for explicit FAIL lines.
    local out
    out="$(npm test -- --run 2>&1)"
    echo "$out"
    if echo "$out" | grep -Eq '(^FAIL )|([[:space:]][1-9][0-9]* failed)'; then
        echo 'web tests have FAIL lines'; return 1
    fi
    npm run build >/dev/null || { echo 'build failed'; return 1; }
}

# Job 5: Documentation internal-link check (CI's exact logic).
stage_doc() {
    local fail=0 f dir target path resolved
    local -a md_files
    mapfile -t md_files < <(find . -type f -name '*.md' -not -path '*/node_modules/*' -not -path './.git/*')
    for f in "${md_files[@]}"; do
        dir="$(dirname "$f")"
        while IFS= read -r target; do
            case "$target" in http://*|https://*|mailto:*) continue ;; esac
            path="${target%%#*}"
            [[ -z "$path" ]] && continue
            resolved="$dir/$path"
            if [[ ! -e "$resolved" ]]; then
                echo "BROKEN: $f -> $target"
                fail=1
            fi
        done < <(grep -oE '\]\([^)]+\)' "$f" | sed -E 's/^\]\(//; s/\)$//')
    done
    [[ "$fail" -eq 0 ]] && echo "doc links OK"
    return "$fail"
}

# Job: whole-DB restore-over-populated-DB guard (ADR-0060/0061).
#
# Added because its absence had teeth: a change to how the scripts resolve the
# superuser password broke this guard outright, and the gate stayed green
# because nothing here ran it. It is the only coverage for
# BackupService.WipeServiceOwnedObjectsAsync, whose failure mode is a
# half-applied restore.
#
# Unlike every other stage this one needs the DEV STACK, because it exercises
# the real Postgres container's ownership topology (superuser-owned extensions,
# non-superuser-owned app objects) rather than an ephemeral throwaway. When the
# stack isn't up it SKIPS rather than fails — a gate that can't be run without
# docker compose up first would just get bypassed. The skip is loud, and
# `skipped` is recorded distinctly from `ok` so a run that didn't cover this
# can't read as one that did.
stage_roundtrip() {
    # DEV_PG_CONTAINER, not PG_CONTAINER — the latter is this script's own
    # ephemeral schema-lane container and must not be confused with the dev
    # stack's. Overridable so the skip path is testable without stopping the
    # dev stack, and so a differently-named stack can still be covered.
    local dev_pg="${DEV_PG_CONTAINER:-coffer-postgres}"
    if ! docker inspect "$dev_pg" >/dev/null 2>&1; then
        echo "SKIPPED: the dev stack is not running (no '$dev_pg' container)."
        echo "  This guard needs it — it reproduces the ownership topology that broke"
        echo "  pg_restore --clean, which an ephemeral container doesn't have."
        echo "  Start it with scripts/dev-up-docker.sh and re-run to cover this."
        return 3
    fi
    PG_CONTAINER="$dev_pg" bash scripts/backup-restore-roundtrip.sh
}

# --------------------------------------------------------------
# Orchestration: launch independent stages in the background, build
# once, then launch the dotnet shards. Each stage logs to its own file
# and records elapsed seconds; we wait on all and aggregate.
# --------------------------------------------------------------
launch() {
    local name="$1"; shift
    ( s=$(date +%s); "$@"; rc=$?; echo "$(( $(date +%s) - s ))" > "$logdir/$name.time"; exit "$rc" ) \
        >"$logdir/$name.log" 2>&1 &
    PID[$name]=$!
}

wall_start=$(date +%s)

# A partial run must never read as a full one, so say so up front AND in the
# verdict at the end.
if [[ -n "$ONLY" ]]; then
    echo "==> PARTIAL RUN — stages:$ONLY"
    echo "    Skipped stages are NOT verified. Run without --only before pushing."
fi

# Build-independent stages start immediately.
if want schema; then
    if [[ "$QUICK" -eq 0 ]]; then
        launch schema stage_schema
    else
        echo "==> Skipping schema-apply (--quick). Only safe when this PR touches NO migration or db/test SQL."
    fi
fi
want audit && launch audit stage_audit
want identity && launch identity stage_identity
want web   && launch web   stage_web
want doc   && launch doc   stage_doc
want roundtrip && launch roundtrip stage_roundtrip

# Build once (foreground) so every dotnet shard can run --no-build. Pointless when
# no shard is selected — this is what makes `--only doc` cost seconds, not minutes.
build_ok=1
need_build=0
for s in test_s1 test_s1b test_s2 test_s3 test_s4 test_importer; do
    want "$s" && need_build=1
done
if [[ "$need_build" -eq 1 ]]; then
    echo "==> dotnet build (Release) — shared by all test shards"
    if ! dotnet build Coffer.slnx --configuration Release >"$logdir/build.log" 2>&1; then
        build_ok=0
    fi
fi

if [[ "$build_ok" -eq 1 ]]; then
    # Namespace-partitioned shards, balanced by measured RUN TIME (2026-07;
    # per-class trx timings), not class count. Integration.Transactions was the
    # whole-suite long pole (~590s), so it's split into two ~equal halves: S1B
    # carries its six heaviest classes (MergeCandidates, InvestmentTransactions-
    # Endpoints, PatchTransaction, BulkTransactions, BalanceMergeHideSync,
    # InKindTransfer ≈ 251s), S1 carries the Transactions remainder (≈239s). S4 is
    # the COMPLEMENT so every Api.Tests test runs in exactly one shard.
    want test_s1 && launch test_s1 stage_dotnet_shard S1 \
        "FullyQualifiedName~Integration.Transactions&FullyQualifiedName!~Integration.Transactions.MergeCandidates&FullyQualifiedName!~Integration.Transactions.InvestmentTransactionsEndpoints&FullyQualifiedName!~Integration.Transactions.PatchTransaction&FullyQualifiedName!~Integration.Transactions.BulkTransactions&FullyQualifiedName!~Integration.Transactions.BalanceMergeHideSync&FullyQualifiedName!~Integration.Transactions.InKindTransfer"
    want test_s1b && launch test_s1b stage_dotnet_shard S1B \
        "FullyQualifiedName~Integration.Transactions.MergeCandidates|FullyQualifiedName~Integration.Transactions.InvestmentTransactionsEndpoints|FullyQualifiedName~Integration.Transactions.PatchTransaction|FullyQualifiedName~Integration.Transactions.BulkTransactions|FullyQualifiedName~Integration.Transactions.BalanceMergeHideSync|FullyQualifiedName~Integration.Transactions.InKindTransfer"
    want test_s2 && launch test_s2 stage_dotnet_shard S2 \
        "FullyQualifiedName~Integration.Auth|FullyQualifiedName~Integration.Accounts|FullyQualifiedName~Integration.Meta"
    want test_s3 && launch test_s3 stage_dotnet_shard S3 \
        "FullyQualifiedName~Integration.FeedConnections|FullyQualifiedName~Integration.Backup|FullyQualifiedName~Integration.Reporting|FullyQualifiedName~Integration.Mcp|FullyQualifiedName~Integration.Ingest"
    want test_s4 && launch test_s4 stage_dotnet_shard S4 \
        "FullyQualifiedName!~Integration.Transactions&FullyQualifiedName!~Integration.Auth&FullyQualifiedName!~Integration.Accounts&FullyQualifiedName!~Integration.Meta&FullyQualifiedName!~Integration.FeedConnections&FullyQualifiedName!~Integration.Backup&FullyQualifiedName!~Integration.Reporting&FullyQualifiedName!~Integration.Mcp&FullyQualifiedName!~Integration.Ingest&FullyQualifiedName!~Integration.Stress"
    want test_importer && launch test_importer stage_importer
fi

# --------------------------------------------------------------
# Wait + aggregate.
# --------------------------------------------------------------
failures=()
skipped=()
[[ "$build_ok" -ne 1 ]] && failures+=("dotnet-build")

for name in "${!PID[@]}"; do
    rc=0
    wait "${PID[$name]}" || rc=$?
    elapsed="$(cat "$logdir/$name.time" 2>/dev/null || echo '?')"
    # rc 3 is the agreed "couldn't run, didn't fail" signal (stage_roundtrip when
    # the dev stack is down). Tracked separately from ok so the summary can't
    # imply coverage that never happened.
    if [[ "$rc" -eq 0 ]]; then
        printf '  %-14s ok    (%ss)\n' "$name" "$elapsed"
    elif [[ "$rc" -eq 3 ]]; then
        printf '  %-14s SKIP  (%ss)\n' "$name" "$elapsed"
        skipped+=("$name")
    else
        printf '  %-14s FAIL  (%ss)\n' "$name" "$elapsed"
        failures+=("$name")
    fi
done

# Surface the logs that matter.
if [[ "$build_ok" -ne 1 ]]; then
    echo "================ build.log ================"
    cat "$logdir/build.log"
fi
for name in "${failures[@]}"; do
    [[ "$name" == "dotnet-build" ]] && continue
    echo "================ $name.log ================"
    cat "$logdir/$name.log" 2>/dev/null || true
done

# A skipped stage's log explains what wasn't covered, and that is worth reading
# even on a green run — silence here is what let a broken restore guard sit
# unnoticed.
for name in "${skipped[@]}"; do
    echo "================ $name.log (SKIPPED) ================"
    cat "$logdir/$name.log" 2>/dev/null || true
done

wall_end=$(date +%s)
echo
echo "wall-clock: $(( wall_end - wall_start ))s"
if [[ "${#failures[@]}" -eq 0 ]]; then
    echo "============================================================"
    if [[ -n "$ONLY" ]]; then
        # Never print the unqualified pass line for a partial run — that string is
        # the push gate, and a green subset says nothing about what was skipped.
        echo " PARTIAL OK (stages:$ONLY) -- NOT a push gate"
        echo " Run scripts/preflight.sh with no --only before pushing."
    elif [[ "${#skipped[@]}" -gt 0 ]]; then
        # Deliberately does NOT contain the string "PREFLIGHT OK": that is the
        # push gate, and a stage that could not run has verified nothing. Saying
        # "OK, with skips" is how a guard ends up trusted without having run —
        # the exact hole that let a broken restore guard sit unnoticed.
        echo " PREFLIGHT INCOMPLETE -- did NOT run: ${skipped[*]}"
        echo " Nothing failed, but those stages verified nothing. See their logs above."
        echo " For the restore guard: scripts/dev-up-docker.sh, then re-run."
    else
        echo " PREFLIGHT OK -- safe to push"
    fi
    echo "============================================================"
    exit 0
else
    echo "============================================================"
    echo " PREFLIGHT FAILED -- do NOT push"
    echo " Failing stages: ${failures[*]}"
    echo "============================================================"
    exit 1
fi
