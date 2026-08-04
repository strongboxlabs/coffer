#!/usr/bin/env bash
# =============================================================================
# ci-dotnet-shards.sh — build the solution ONCE, then run the .NET test suite as
# parallel sharded processes (each its own env-var space + its own
# Testcontainers Postgres), aggregate, and exit non-zero if any shard fails.
# =============================================================================
#
# The coffer-dev CI runner is a SINGLE self-hosted runner (one job at a time), so
# a GitHub matrix of test shards runs SEQUENTIALLY and each shard job re-runs
# `dotnet build` (~90s × 6 ≈ 9 min of redundant building). This mirrors
# scripts/preflight.sh's proven dotnet-shard harness so CI builds once and runs
# the shards in parallel on the runner's cores instead.
#
# Shard partition is identical to preflight.sh (namespace-based, balanced by
# measured run time; S4 is the COMPLEMENT so every Api.Tests test runs in exactly
# one shard). Assumes a .NET SDK on PATH and the repo checked out.
# =============================================================================
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

logdir="$(mktemp -d)"
declare -A PID

launch() {
    local name="$1"; shift
    ( s=$(date +%s); "$@"; rc=$?; echo "$(( $(date +%s) - s ))" > "$logdir/$name.time"; exit "$rc" ) \
        >"$logdir/$name.log" 2>&1 &
    PID[$name]=$!
}

api_shard() {
    dotnet test tests/Api.Tests/Api.Tests.csproj --configuration Release --no-build \
        --filter "$1" --logger "console;verbosity=minimal"
}
importer_shard() {
    dotnet test tests/Importer.Moneydance.Tests/Importer.Moneydance.Tests.csproj \
        --configuration Release --no-build --logger "console;verbosity=minimal"
}

wall_start=$(date +%s)

echo "==> dotnet build (Release) — shared by all shards"
if ! dotnet build Coffer.slnx --configuration Release >"$logdir/build.log" 2>&1; then
    cat "$logdir/build.log"
    echo "BUILD FAILED"
    exit 1
fi

# Shard filters — MUST match scripts/preflight.sh.
launch s1 api_shard \
    "FullyQualifiedName~Integration.Transactions&FullyQualifiedName!~Integration.Transactions.MergeCandidates&FullyQualifiedName!~Integration.Transactions.InvestmentTransactionsEndpoints&FullyQualifiedName!~Integration.Transactions.PatchTransaction&FullyQualifiedName!~Integration.Transactions.BulkTransactions&FullyQualifiedName!~Integration.Transactions.BalanceMergeHideSync&FullyQualifiedName!~Integration.Transactions.InKindTransfer"
launch s1b api_shard \
    "FullyQualifiedName~Integration.Transactions.MergeCandidates|FullyQualifiedName~Integration.Transactions.InvestmentTransactionsEndpoints|FullyQualifiedName~Integration.Transactions.PatchTransaction|FullyQualifiedName~Integration.Transactions.BulkTransactions|FullyQualifiedName~Integration.Transactions.BalanceMergeHideSync|FullyQualifiedName~Integration.Transactions.InKindTransfer"
launch s2 api_shard \
    "FullyQualifiedName~Integration.Auth|FullyQualifiedName~Integration.Accounts|FullyQualifiedName~Integration.Meta"
launch s3 api_shard \
    "FullyQualifiedName~Integration.FeedConnections|FullyQualifiedName~Integration.Backup|FullyQualifiedName~Integration.Reporting|FullyQualifiedName~Integration.Mcp|FullyQualifiedName~Integration.Ingest"
launch s4 api_shard \
    "FullyQualifiedName!~Integration.Transactions&FullyQualifiedName!~Integration.Auth&FullyQualifiedName!~Integration.Accounts&FullyQualifiedName!~Integration.Meta&FullyQualifiedName!~Integration.FeedConnections&FullyQualifiedName!~Integration.Backup&FullyQualifiedName!~Integration.Reporting&FullyQualifiedName!~Integration.Mcp&FullyQualifiedName!~Integration.Ingest&FullyQualifiedName!~Integration.Stress"
launch importer importer_shard

failures=()
for name in "${!PID[@]}"; do
    if wait "${PID[$name]}"; then
        printf '  %-10s ok    (%ss)\n' "$name" "$(cat "$logdir/$name.time" 2>/dev/null || echo '?')"
    else
        printf '  %-10s FAIL  (%ss)\n' "$name" "$(cat "$logdir/$name.time" 2>/dev/null || echo '?')"
        failures+=("$name")
    fi
done

for name in "${failures[@]}"; do
    echo "================ $name.log ================"
    cat "$logdir/$name.log" 2>/dev/null || true
done

echo
echo "dotnet shards wall-clock: $(( $(date +%s) - wall_start ))s"
if [[ "${#failures[@]}" -eq 0 ]]; then
    echo "ALL SHARDS OK"
    exit 0
fi
echo "SHARDS FAILED: ${failures[*]}"
exit 1
