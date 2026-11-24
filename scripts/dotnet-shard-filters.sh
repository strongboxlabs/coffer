#!/usr/bin/env bash
# =============================================================================
# dotnet-shard-filters.sh — the SINGLE definition of the .NET test shards.
# =============================================================================
#
# Sourced by scripts/ci-dotnet-shards.sh (CI, including the public repo's own
# ci.yml) and by the maintainer's local pre-push gate. Not executable on its
# own.
#
# WHY IT EXISTS. One definition for both callers: with a copy in each, narrowing
# a shard in one file and not the other drops tests from that run silently, and
# a green result then means less than it appears to.
#
# -----------------------------------------------------------------------------
# THE PARTITION
# -----------------------------------------------------------------------------
# The shards run in PARALLEL inside one job, so the job costs what the SLOWEST
# shard costs. Balancing them is the whole game; total test time is irrelevant.
#
# A class is the smallest unit a shard can be given, and what a class costs is
# only known by measuring it on the machine, under the load the shards run
# under, nine at a time. So the partition is DATA, not a hand-written list:
#
#   scripts/dotnet-shard-assignment.tsv    shard <TAB> seconds <TAB> class
#
# is written by the maintainer's rebalance script from the per-class table
# every CI run prints (ci-dotnet-shards.sh, between its CLASS TIMINGS markers),
# by packing the costliest classes first, each into the shard with the least so
# far. It is committed, so a rebalance is a reviewed change. The filters below are
# derived from it; nothing here names a namespace.
#
# To rebalance after tests have been added or have changed cost, the maintainer
# runs the rebalance script on a green CI run, then the shard coverage check, and
# commits the result.
#
# The shard NAMES (trx_a ... rest) are the public CI's matrix entries and are kept
# for that reason. They no longer say what a shard holds.
#
# -----------------------------------------------------------------------------
# THE COMPLEMENT PROPERTY — do not break this
# -----------------------------------------------------------------------------
# `rest` runs every class no OTHER shard names: it excludes by name what the other
# shards list, so its own assigned classes are in it, and a test class added after
# the last rebalance falls into it AUTOMATICALLY and can never be silently skipped.
# It runs until the next rebalance gives it a shard of its own, so a rebalance is a
# cost matter and never a coverage one. Its own classes are in the assignment for
# their cost, which the rebalance packs; its filter does not need to name them.
#
# ONE DELIBERATE CARVE-OUT: Integration.Stress is in no shard, so those tests run
# in no shard at all. That is intended. They are the cost/performance boundary
# tests and they have their own dedicated lane, deliberately outside the gate.
#
# A class is matched by its full name followed by a dot (the dot that precedes the
# test method), so a class never matches another whose name merely starts the same.
#
# VERIFY, DON'T TRUST. After ANY edit here or to the assignment, prove the union of
# these filters is every test exactly once (the maintainer's shard coverage check
# lists the tests each filter selects and diffs that against the full suite). A typo
# or a forgotten exclusion drops tests from every shard, and the run still reports
# green, which is the one thing a test gate must never do.
# =============================================================================

_sf_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
_sf_assignment="$_sf_dir/dotnet-shard-assignment.tsv"
if [[ ! -s "$_sf_assignment" ]]; then
    echo "dotnet-shard-filters: missing or empty $_sf_assignment — cannot determine the" >&2
    echo "  shard partition, and running an arbitrary subset would report a green" >&2
    echo "  result for tests that never ran." >&2
    exit 2
fi

# Every class is under this root; dropping it keeps the filters short enough for a
# Windows command line, which a local run may use.
_sf_root='Coffer.Api.Tests.'
declare -A _sf_own=()
_sf_not=''
while IFS=$'\t' read -r _sf_shard _sf_secs _sf_class; do
    [[ -n "$_sf_shard" && -n "$_sf_class" && "$_sf_shard" != \#* ]] || continue
    _sf_short="${_sf_class#"$_sf_root"}"
    _sf_own[$_sf_shard]+="${_sf_own[$_sf_shard]:+|}FullyQualifiedName~${_sf_short}."
    [[ "$_sf_shard" == rest ]] || _sf_not+="${_sf_not:+&}FullyQualifiedName!~${_sf_short}."
done <"$_sf_assignment"

for _sf_name in trx_a trx_b trx_c feeds accounts backup ingest; do
    if [[ -z "${_sf_own[$_sf_name]:-}" ]]; then
        echo "dotnet-shard-filters: shard '$_sf_name' has no class in $_sf_assignment" >&2
        exit 2
    fi
    printf -v "SHARD_FILTER_$_sf_name" '%s' "${_sf_own[$_sf_name]}"
done

# The complement shard: anything no other shard names, but Stress, which is never
# assigned.
SHARD_FILTER_rest="${_sf_not}&FullyQualifiedName!~Integration.Stress"

# A filter is one command-line argument, and a Windows command line has a fixed
# length (_sf_windows_max), all arguments together. Past that, a local run cannot
# start the test process, and a listing of the shard comes back empty. So a partition
# whose filter cannot fit, with room left for the rest of the dotnet test invocation
# (_sf_limit), stops here, by name. The complement is the longest filter, one clause
# per class the other shards hold: when this trips, shorten the clauses (a longer
# common root), not the partition.
_sf_windows_max=32767
_sf_limit=$(( _sf_windows_max - 2767 ))
for _sf_name in trx_a trx_b trx_c feeds accounts backup ingest rest; do
    _sf_var="SHARD_FILTER_$_sf_name"
    _sf_value="${!_sf_var}"
    if (( ${#_sf_value} > _sf_limit )); then
        echo "dotnet-shard-filters: shard '$_sf_name' filter is ${#_sf_value} characters," >&2
        echo "  over $_sf_limit, so a Windows command line cannot carry it with the rest of" >&2
        echo "  the dotnet test invocation, and the shard would list no tests." >&2
        exit 2
    fi
done

# The importer shard: the whole Importer.Moneydance.Tests project but its own Stress
# namespace, the same carve-out, and run by the same lane. It is a different project, so
# a namespace is the only partition it needs. Deliberately NOT a SHARD_FILTER_* name:
# every SHARD_FILTER_* is an Api.Tests filter, and the shard coverage check and the
# pre-push changed stage apply each of them to that project.
IMPORTER_FILTER='FullyQualifiedName!~Coffer.Importer.Moneydance.Tests.Stress.'

unset _sf_dir _sf_assignment _sf_root _sf_own _sf_not _sf_shard _sf_secs _sf_class _sf_short _sf_name \
    _sf_windows_max _sf_limit _sf_var _sf_value
