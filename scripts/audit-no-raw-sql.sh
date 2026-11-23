#!/usr/bin/env bash
# Audit src/Api for raw-SQL escapes and fail unless each is tagged
# with an APPROVED-RAW-SQL-EXCEPTION comment within the preceding
# few lines.
#
# The policy is docs/decisions/0005-dapper-and-efcore.md: the API
# data-access layer goes through LINQ + EF; complex SQL lives in
# Postgres functions/views declared in db/migrations/ and is bound via
# HasDbFunction. What this script does and does NOT cover is below.
#
# SCOPE, so a green run isn't over-read: src/Api ONLY.
#   * src/Importer.Moneydance is deliberately Dapper (ADR-0005:
#     large bulk inserts, unnest() array params, deferred
#     constraints). Not audited, not a violation.
#   * tests/ is not audited — fixtures need DDL (CREATE DATABASE,
#     GRANT, CREATE EXTENSION, TRUNCATE) that has no EF analogue.
#
# Hits returned by this script are either (a) pre-approved exceptions
# (tagged) or (b) violations. Resolve (b) by moving the SQL into a
# Postgres function, or — when the code must run BEFORE migrations,
# where no migration-created function can exist yet — by using plain
# EF with a narrow catch. Tagging a new exception instead is a reviewed
# change of its own, with its reason in the tag; the bar is high (exactly
# one exists, the RLS session interceptor).
#
# Usage:
#   scripts/audit-no-raw-sql.sh          # report + exit 1 on violations
#
# Wired into .github/workflows/ci.yml as the `api-no-raw-sql-audit` job.

set -euo pipefail

# Run from the repo root whatever the caller's cwd is. With a bare relative
# 'src/Api', grep fails from any other directory, the `|| true` below swallows
# the failure, and the audit prints OK having scanned nothing.
cd "$(dirname "$0")/.."

AUDIT_ROOT='src/Api'
TOKEN='APPROVED-RAW-SQL-EXCEPTION'
WINDOW=20  # lines of context before the match to scan for the approval token

# The escapes this audit looks for, each paired with a line it MUST match. The
# pairing is the point: the joined regex below is the entire job of this script,
# so a pattern that quietly stops matching turns every future run green — one
# invisible control byte in a pattern is enough.
#
# EF Core's interpolated forms take SQL text as surely as the Raw ones do — the
# parameters are bound, the statement is still hand-written — so they are
# escapes too: FromSql, Database.SqlQuery<T>, ExecuteSql and ExecuteSqlAsync.
#
# So do ADO.NET's other doors: a command made with its text in one call
# (NpgsqlDataSource.CreateCommand("…"), so CreateCommand with any arguments), a
# batch and its commands (NpgsqlBatch, NpgsqlBatchCommand), and the text set on a
# command however it was obtained (CommandText).
RAW_SQL_PATTERNS=(
    'FromSqlRaw'
    'SqlQueryRaw'
    'ExecuteSqlRaw'
    'ExecuteSqlInterpolated'
    'FromSqlInterpolated'
    'NpgsqlCommand'
    'CreateCommand\('
    'using Dapper'
    'FromSql\('
    'SqlQuery<'
    'ExecuteSql\('
    'ExecuteSqlAsync\('
    'NpgsqlBatch'
    'CommandText'
)
RAW_SQL_PROBES=(
    'var rows = db.Things.FromSqlRaw("select 1");'
    'var n = db.Database.SqlQueryRaw<int>("select 1");'
    'await db.Database.ExecuteSqlRaw("update t set c = 1");'
    'await db.Database.ExecuteSqlInterpolated($"update t set c = {v}");'
    'var rows = db.Things.FromSqlInterpolated($"select {v}");'
    'await using var cmd = (NpgsqlCommand)conn.CreateCommand();'
    'await using var cmd = dataSource.CreateCommand("delete from things");'
    'using Dapper;'
    'var rows = db.Things.FromSql($"select * from things where id = {id}");'
    'var ids = await db.Database.SqlQuery<Guid>($"select id from things").ToListAsync();'
    'db.Database.ExecuteSql($"update things set c = {v}");'
    'await db.Database.ExecuteSqlAsync($"update things set c = {v}", ct);'
    'batch.BatchCommands.Add(new NpgsqlBatchCommand("update things set c = 1"));'
    'command.CommandText = "update things set c = 1";'
)

PATTERNS="$(IFS='|'; printf '%s' "${RAW_SQL_PATTERNS[*]}")"

# ---------------------------------------------------------------------------
# The scan. A function so the self-test below can drive the REAL code path
# against fixtures instead of a second copy of the window logic — a self-test
# that restates the logic tests the restatement.
#
# Sets VIOLATION_COUNT; prints each violation.
# ---------------------------------------------------------------------------
VIOLATION_COUNT=0
scan_for_violations() {
    local root="$1"
    local -a hits=()
    mapfile -t hits < <(grep -rnE --include='*.cs' "$PATTERNS" "$root" || true)

    local violations=0 hit file rest line content start
    for hit in "${hits[@]}"; do
        [[ -z "$hit" ]] && continue
        file="${hit%%:*}"
        rest="${hit#*:}"
        line="${rest%%:*}"
        content="${rest#*:}"

        start=$(( line - WINDOW ))
        [[ $start -lt 1 ]] && start=1

        # Scan the window before (and including) the match for the approval
        # token. Tolerate sed exit codes — we only care whether the token
        # appears.
        window_text="$(sed -n "${start},${line}p" "$file" 2>/dev/null)" || true
        if ! grep -q "$TOKEN" <<<"$window_text"; then
            if [[ $violations -eq 0 ]]; then
                echo "Raw-SQL audit failed ($root/):"
                echo
            fi
            echo "  $file:$line"
            echo "    $content"
            violations=$(( violations + 1 ))
        fi
    done
    VIOLATION_COUNT=$violations
}

# ---------------------------------------------------------------------------
# SELF-TEST — prove the audit can still fail, before trusting that it didn't.
#
# This is an ABSENCE check: it greps, finds nothing, prints OK. That is
# indistinguishable from a check that finds nothing because it is BROKEN — a
# search tool that is not installed, for one, finds nothing and passes.
#
# Runs on every invocation, never behind a flag: a self-test you have to
# remember to run fails the same way as the bug it looks for.
# ---------------------------------------------------------------------------
selftest_fail=0

if [[ "${#RAW_SQL_PATTERNS[@]}" -ne "${#RAW_SQL_PROBES[@]}" ]]; then
    echo "SELF-TEST FAILED: ${#RAW_SQL_PATTERNS[@]} patterns but ${#RAW_SQL_PROBES[@]} probes." >&2
    echo "  Every pattern needs a line proving it still matches." >&2
    exit 1
fi

# (a) Every pattern must still match its probe, both alone and inside the joined
# regex the scan actually uses.
for i in "${!RAW_SQL_PATTERNS[@]}"; do
    if ! grep -qE "${RAW_SQL_PATTERNS[$i]}" <<<"${RAW_SQL_PROBES[$i]}"; then
        echo "SELF-TEST FAILED: pattern '${RAW_SQL_PATTERNS[$i]}' no longer matches" >&2
        echo "  its probe: ${RAW_SQL_PROBES[$i]}" >&2
        selftest_fail=1
    fi
    if ! grep -qE "$PATTERNS" <<<"${RAW_SQL_PROBES[$i]}"; then
        echo "SELF-TEST FAILED: the joined regex misses '${RAW_SQL_PATTERNS[$i]}'." >&2
        echo "  Joining the alternatives corrupted one of them." >&2
        selftest_fail=1
    fi
done

# (b) ...and must NOT match ordinary EF. An audit that cries wolf gets muted,
# which is its own way of not being an audit.
while IFS= read -r clean_line; do
    [[ -z "$clean_line" ]] && continue
    if grep -qE "$PATTERNS" <<<"$clean_line"; then
        echo "SELF-TEST FAILED: the audit flags ordinary EF: $clean_line" >&2
        selftest_fail=1
    fi
done <<'CLEAN_EF'
var rows = await db.Things.Where(t => t.Id == id).ToListAsync();
await db.SaveChangesAsync(ct);
var n = await db.Things.CountAsync(ct);
await db.Things.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
await db.Things.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.C, 1), ct);
var rows = db.HoldingsAsOfNow(ledgerId, at, null).FromExpression();
CLEAN_EF

# (c) The approval/window logic must work in BOTH directions. A token that
# stopped being honoured is loud and self-correcting — everything turns into a
# violation. A window that silently approves too much is neither, so that is the
# direction worth proving: the fixtures below include a hit whose token sits
# outside WINDOW, which MUST still count.
#
# THE FIXTURES ARE LITERALS ON PURPOSE. Built from "$TOKEN" and from
# $(( WINDOW + 5 )), they would make this arm untestable: widen WINDOW and the
# filler grows with it, so the hit stays outside the window and the count never
# changes; rename TOKEN and the fixture renames too, so it still matches. A
# fixture derived from the value it is meant to test cannot detect a change to
# that value.
#
# The cost of pinning them is that changing WINDOW or TOKEN now fails here until
# this block is updated too — which is the point. Those are policy changes, not
# refactors.
SELFTEST_TOKEN='APPROVED-RAW-SQL-EXCEPTION'
SELFTEST_FAR=40   # filler lines; must stay comfortably above WINDOW

if [[ "$SELFTEST_TOKEN" != "$TOKEN" ]]; then
    echo "SELF-TEST FAILED: TOKEN is '$TOKEN' but the fixtures below are pinned to" >&2
    echo "  '$SELFTEST_TOKEN'. Renaming the token is a policy change — update the" >&2
    echo "  fixtures deliberately rather than letting them follow it." >&2
    selftest_fail=1
fi
if [[ "$WINDOW" -ge "$SELFTEST_FAR" ]]; then
    echo "SELF-TEST FAILED: WINDOW=$WINDOW has reached the fixture distance" >&2
    echo "  ($SELFTEST_FAR), so the 'token too far away' fixture no longer sits" >&2
    echo "  outside the window and proves nothing. Raise SELFTEST_FAR too." >&2
    selftest_fail=1
fi

selftest_dir="$(mktemp -d)"
trap 'rm -rf "$selftest_dir"' EXIT

printf '// APPROVED-RAW-SQL-EXCEPTION (2026-01-01): rationale\nawait using var cmd = (NpgsqlCommand)conn.CreateCommand();\n' \
    > "$selftest_dir/Approved.cs"

printf 'await using var cmd = (NpgsqlCommand)conn.CreateCommand();\n' \
    > "$selftest_dir/Untagged.cs"

{
    printf '// APPROVED-RAW-SQL-EXCEPTION (2026-01-01): rationale\n'
    for _ in $(seq 1 "$SELFTEST_FAR"); do printf '// filler\n'; done
    printf 'await using var cmd = (NpgsqlCommand)conn.CreateCommand();\n'
} > "$selftest_dir/TooFarAway.cs"

scan_for_violations "$selftest_dir" >/dev/null
if [[ "$VIOLATION_COUNT" -ne 2 ]]; then
    echo "SELF-TEST FAILED: expected 2 violations from the fixtures — an untagged" >&2
    echo "  hit, and one whose token sits beyond WINDOW=$WINDOW — but got" >&2
    echo "  $VIOLATION_COUNT. Either a token is approving more than it should," >&2
    echo "  or tokens have stopped being honoured at all." >&2
    selftest_fail=1
fi

rm -rf "$selftest_dir"
trap - EXIT

# (d) The tree must actually be there. grep over a missing or half-checked-out
# directory finds nothing, which reads exactly like a clean audit.
mapfile -t tracked_cs < <(git ls-files -- "$AUDIT_ROOT/*.cs" 2>/dev/null || true)
if [[ "${#tracked_cs[@]}" -eq 0 ]]; then
    echo "SELF-TEST FAILED: git tracks no .cs files under $AUDIT_ROOT/." >&2
    echo "  Zero files in scope is not a clean audit — it is a scan with nothing" >&2
    echo "  to look at: wrong directory, or an incomplete checkout." >&2
    selftest_fail=1
else
    missing=0
    for f in "${tracked_cs[@]}"; do
        [[ -f "$f" ]] || missing=$(( missing + 1 ))
    done
    if [[ "$missing" -gt 0 ]]; then
        echo "SELF-TEST FAILED: $missing of ${#tracked_cs[@]} tracked .cs files under" >&2
        echo "  $AUDIT_ROOT/ are missing from disk, so the audit would skip them." >&2
        selftest_fail=1
    fi
fi

if [[ "$selftest_fail" -ne 0 ]]; then
    echo >&2
    echo "audit-no-raw-sql.sh FAILED ITS OWN SELF-TEST. The audit is broken, so a" >&2
    echo "clean result from it would mean nothing. Fix the check, then re-run." >&2
    exit 1
fi

# --- The audit ---------------------------------------------------------------
scan_for_violations "$AUDIT_ROOT"

if [[ "$VIOLATION_COUNT" -gt 0 ]]; then
    echo
    echo "Resolve each hit, in order of preference:"
    echo "  (a) move it into a Postgres function in db/migrations/ and"
    echo "      bind via HasDbFunction;"
    echo "  (b) if the code must run BEFORE migrations — where no"
    echo "      migration-created function can exist yet — use plain EF"
    echo "      with a narrow catch instead;"
    echo "  (c) tag it '$TOKEN' + date + rationale"
    echo "      within the preceding $WINDOW lines. (c) is a reviewed change"
    echo "      of its own, never folded into the change that needs it."
    echo
    echo "Policy: docs/decisions/0005-dapper-and-efcore.md"
    echo "Scope: the header of scripts/audit-no-raw-sql.sh."
    exit 1
fi

echo "Raw-SQL audit OK — no unsanctioned raw-SQL in ${#tracked_cs[@]} tracked $AUDIT_ROOT/ files (self-test passed)."
