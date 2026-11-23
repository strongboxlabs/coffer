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
# `rest` runs its own assigned classes AND everything the assignment does not
# name: it excludes by name what the other shards (and itself) list, so a test
# class added after the last rebalance falls into it AUTOMATICALLY and can never
# be silently skipped. It runs until the next rebalance gives it a shard of its
# own, so a rebalance is a cost matter and never a coverage one.
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
    _sf_not+="${_sf_not:+&}FullyQualifiedName!~${_sf_short}."
done <"$_sf_assignment"

for _sf_name in trx_a trx_b trx_c feeds accounts backup ingest; do
    if [[ -z "${_sf_own[$_sf_name]:-}" ]]; then
        echo "dotnet-shard-filters: shard '$_sf_name' has no class in $_sf_assignment" >&2
        exit 2
    fi
    printf -v "SHARD_FILTER_$_sf_name" '%s' "${_sf_own[$_sf_name]}"
done

# The complement shard: its own classes, or anything no shard names. Stress is
# subtracted from the second part only; it is never assigned.
SHARD_FILTER_rest="(${_sf_own[rest]:-FullyQualifiedName~Integration.NoSuchNamespace.})|(${_sf_not}&FullyQualifiedName!~Integration.Stress)"

unset _sf_dir _sf_assignment _sf_root _sf_own _sf_not _sf_shard _sf_secs _sf_class _sf_short _sf_name
