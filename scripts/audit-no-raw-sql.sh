#!/usr/bin/env bash
# Audit src/Api for raw-SQL escapes and fail unless each is tagged
# with an APPROVED-RAW-SQL-EXCEPTION comment within the preceding
# few lines.
#
# The policy is docs/decisions/0005-dapper-and-efcore.md (as realigned
# in PR 3.6.5): the API data-access layer goes through LINQ + EF;
# complex SQL lives in Postgres functions/views declared in
# db/migrations/ and is bound via HasDbFunction. Operational notes —
# what this script does and does NOT cover — are in the memory entry
# `feedback_no_raw_sql_in_api`.
#
# SCOPE, so a green run isn't over-read: src/Api ONLY.
#   * src/Importer.Moneydance is deliberately Dapper (ADR-0005:
#     108k-row bulk inserts, unnest() array params, deferred
#     constraints). Not audited, not a violation.
#   * tests/ is not audited — fixtures need DDL (CREATE DATABASE,
#     GRANT, CREATE EXTENSION, TRUNCATE) that has no EF analogue.
#
# Hits returned by this script are either (a) pre-approved exceptions
# (tagged) or (b) violations. Resolve (b) by moving the SQL into a
# Postgres function, or — when the code must run BEFORE migrations,
# where no migration-created function can exist yet — by using plain
# EF with a narrow catch. Tagging a new exception instead requires
# explicit sign-off from the repo owner; the bar is high (exactly one
# exists today, the RLS session interceptor).
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
    echo "Resolve each hit, in order of preference:"
    echo "  (a) move it into a Postgres function in db/migrations/ and"
    echo "      bind via HasDbFunction;"
    echo "  (b) if the code must run BEFORE migrations — where no"
    echo "      migration-created function can exist yet — use plain EF"
    echo "      with a narrow catch instead;"
    echo "  (c) tag it '$TOKEN' + date + rationale"
    echo "      within the preceding $WINDOW lines. (c) requires explicit"
    echo "      sign-off from the repo owner — do not self-approve."
    echo
    echo "Policy: docs/decisions/0005-dapper-and-efcore.md"
    echo "Scope + operational notes: feedback_no_raw_sql_in_api in project memory."
    exit 1
fi

echo "Raw-SQL audit OK — no unsanctioned raw-SQL in src/Api/."
