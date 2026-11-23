#!/usr/bin/env bash
# =============================================================================
# test-class-timings.sh — seconds of test time per test class, from .trx files.
# =============================================================================
#
# WHY. The test shards run in parallel inside one job, so the job costs what its
# slowest shard costs (scripts/dotnet-shard-filters.sh). Balancing them needs the
# cost of each test class, and the cost of a class is only known by measuring it
# on the machine and under the load the shards run under.
#
# WHAT. Reads the .trx files the shards write and prints one line per class:
#
#     <seconds>\t<namespace.Class>
#
# largest first. A class's seconds are the sum of its tests' durations; the tests
# of one class run one after another, so the sum is what the class costs a shard.
# A theory's cases are one test method each in a trx, and the class is what is left
# when the case arguments and the method name are removed.
#
# Usage:  scripts/test-class-timings.sh <file.trx>...
# Exit:   0 with output · 2 when no trx has a test result (nothing measured)
# =============================================================================
set -uo pipefail

if [[ $# -eq 0 ]]; then
    echo "test-class-timings: no .trx file given" >&2
    exit 2
fi

# A trx result element carries its name and duration as attributes of one line:
#   <UnitTestResult ... testName="Ns.Class.Method(arg: x)" ... duration="hh:mm:ss.fffffff" ...>
# The class is the name up to the argument list, minus its last dotted segment.
awk '
    /<UnitTestResult / {
        if (!match($0, /testName="[^"]*"/)) next
        name = substr($0, RSTART + 10, RLENGTH - 11)
        if (!match($0, /duration="[^"]*"/)) next
        dur = substr($0, RSTART + 10, RLENGTH - 11)
        n = split(dur, t, ":")
        if (n != 3) next
        secs = t[1] * 3600 + t[2] * 60 + t[3]
        paren = index(name, "(")
        if (paren > 0) name = substr(name, 1, paren - 1)
        dot = 0
        for (i = length(name); i > 0; i--) if (substr(name, i, 1) == ".") { dot = i; break }
        if (dot == 0) next
        total[substr(name, 1, dot - 1)] += secs
        seen = 1
    }
    END {
        if (!seen) exit 2
        for (c in total) printf "%.1f\t%s\n", total[c], c
    }
' "$@" | sort -t$'\t' -k1,1 -nr
# The pipeline's status is sort's; awk's exit 2 (nothing measured) is the one to keep.
rc=${PIPESTATUS[0]}
exit "$rc"
