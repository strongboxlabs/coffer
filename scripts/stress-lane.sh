#!/usr/bin/env bash
# =============================================================================
# stress-lane.sh — the on-demand scale lane.
# =============================================================================
#
# Runs the Integration.Stress tests, which every other lane deliberately skips:
# preflight.sh and ci-dotnet-shards.sh both exclude the namespace from the s4
# catch-all shard, so a ~50k-transaction seed never lands in a PR's critical path.
#
# Why on demand: the fixture is large by design (that is the point), so it costs
# minutes rather than seconds. The trade-off is that a latency regression will not
# fail the PR that caused it — run this after touching snapshots, the restore
# function, the balance rebuild, or recompute_holdings_cost_basis.
#
#   scripts/stress-lane.sh              # run the lane
#   scripts/stress-lane.sh --filter X   # narrow within the lane
#
# Timings come from ITestOutputHelper, which the console logger only surfaces for
# PASSING tests at `detailed` verbosity — the numbers are the actual output here,
# so anything quieter defeats the purpose. The assertions only guard the ceiling.
# =============================================================================
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

filter="FullyQualifiedName~Integration.Stress"
if [ "${1:-}" = "--filter" ]; then
    [ -n "${2:-}" ] || { echo "stress-lane: --filter needs a value" >&2; exit 1; }
    filter="$filter&FullyQualifiedName~$2"
fi

echo "[stress-lane] Docker must be running (Testcontainers Postgres)."
echo "[stress-lane] filter: $filter"
echo

> "$repo_root/.stress-lane.log"
trap 'echo; echo "[stress-lane] timings:"; grep -E "^ +(seed|seeded|create|restore|fifo):" "$repo_root/.stress-lane.log" || echo "  (none captured — see .stress-lane.log)"' EXIT

# ASP.NET request logging would bury the timings, so tee the full run to a log and
# print just the measurements at the end (the log keeps everything for diagnosis).
dotnet test tests/Api.Tests/Api.Tests.csproj \
    --filter "$filter" \
    --logger "console;verbosity=detailed" \
    --nologo 2>&1 | tee -a "$repo_root/.stress-lane.log" | grep -E "Passed!|Failed!|error|Passed |Failed "
