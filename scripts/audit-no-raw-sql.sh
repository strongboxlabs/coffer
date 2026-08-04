#!/usr/bin/env bash
# Audit src/Api for raw-SQL escapes and fail unless each is tagged
# with an APPROVED-RAW-SQL-EXCEPTION comment within the preceding
# few lines.
#
# See memory entry `feedback_no_raw_sql_in_api` for the policy: the
# API data-access layer goes through LINQ + EF; complex SQL lives in
# Postgres functions/views declared in db/migrations/ and is bound
# via HasDbFunction. Hits returned by this script are either (a)
# pre-approved exceptions (tagged) or (b) violations that need to be
# moved into a Postgres function.
#
# Usage:
#   scripts/audit-no-raw-sql.sh          # report + exit 1 on violations
#
# Wired into .github/workflows/ci.yml as the `api-no-raw-sql` job.

set -euo pipefail

PATTERNS='FromSqlRaw|SqlQueryRaw|ExecuteSqlRaw|ExecuteSqlInterpolated|FromSqlInterpolated|NpgsqlCommand|CreateCommand\(\)|using Dapper'
TOKEN='APPROVED-RAW-SQL-EXCEPTION'
WINDOW=20  # lines of context before the match to scan for the approval token

# Find every hit. `grep -rnE` is available everywhere (CI's Ubuntu,
# git-bash on Windows, mac); previously used `rg` which silently
# false-passed when ripgrep wasn't installed locally — that's a
# bulletproofness regression we don't want. `grep` exits non-zero
# when no matches; we tolerate that as a clean audit.
mapfile -t hits < <(
    grep -rnE --include='*.cs' "$PATTERNS" src/Api || true
)

violations=0
for hit in "${hits[@]}"; do
    file="${hit%%:*}"
    rest="${hit#*:}"
    line="${rest%%:*}"
    content="${rest#*:}"

    start=$(( line - WINDOW ))
    [[ $start -lt 1 ]] && start=1

    # Scan the window before (and including) the match for the
    # approval token. Tolerate sed exit codes — we only care about
    # whether the token appears.
    if ! sed -n "${start},${line}p" "$file" 2>/dev/null | grep -q "$TOKEN"; then
        if [[ $violations -eq 0 ]]; then
            echo "Raw-SQL audit failed (src/Api/):"
            echo
        fi
        echo "  $file:$line"
        echo "    $content"
        violations=$(( violations + 1 ))
    fi
done

if [[ $violations -gt 0 ]]; then
    echo
    echo "Each hit must either (a) be moved into a Postgres function"
    echo "in db/migrations/ and bound via HasDbFunction, or (b) be"
    echo "tagged with '$TOKEN' + a date + rationale within the"
    echo "preceding $WINDOW lines, with explicit user sign-off."
    echo
    echo "See: feedback_no_raw_sql_in_api in project memory."
    exit 1
fi

echo "Raw-SQL audit OK — no unsanctioned raw-SQL in src/Api/."
