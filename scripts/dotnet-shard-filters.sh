#!/usr/bin/env bash
# =============================================================================
# dotnet-shard-filters.sh — the SINGLE definition of the .NET test shards.
# =============================================================================
#
# Sourced by scripts/ci-dotnet-shards.sh (CI, including the public repo's own
# ci.yml) and by the maintainer's local pre-push gate. Not executable on its
# own.
#
# WHY IT EXISTS. These strings were duplicated into both callers, and
# ci-dotnet-shards.sh carried a comment saying the filters "stay defined here,
# once, for both modes" — which was not true, and is the kind of comment that
# keeps a divergence invisible. They were still identical when this was
# extracted, so nothing had broken yet. The failure it prevents: narrowing a
# shard in one file and not the other drops tests from that run silently, and
# a green result then means less than it appears to.
#
# THE PARTITION. s4 is the complement of s1..s3 — it excludes by name every
# namespace those shards include, so a newly added Integration/<Folder> falls
# into s4 automatically and can never be silently skipped. That property is the
# whole reason for the `!~` chain, and it holds only while the two lists agree:
# adding a namespace to s1..s3 means adding it to s4's exclusions in the same
# edit.
#
# ONE DELIBERATE CARVE-OUT: s4 ALSO excludes Integration.Stress, which no shard
# includes — so those tests run in no shard at all. That is intended. They are
# the cost/performance boundary tests and they have their own dedicated lane,
# deliberately outside the gate. Describing s4 as simply "the complement of
# s1..s3" without saying this is what made it read as a coverage hole on
# inspection.
#
# s1 + s1b together cover Integration.Transactions: s1 takes the namespace
# minus six heavy sub-namespaces, s1b takes exactly those six. Splitting them
# was a wall-clock decision (per-class trx timings), not a coverage one.
# =============================================================================

SHARD_FILTER_s1='FullyQualifiedName~Integration.Transactions&FullyQualifiedName!~Integration.Transactions.MergeCandidates&FullyQualifiedName!~Integration.Transactions.InvestmentTransactionsEndpoints&FullyQualifiedName!~Integration.Transactions.PatchTransaction&FullyQualifiedName!~Integration.Transactions.BulkTransactions&FullyQualifiedName!~Integration.Transactions.BalanceMergeHideSync&FullyQualifiedName!~Integration.Transactions.InKindTransfer'

SHARD_FILTER_s1b='FullyQualifiedName~Integration.Transactions.MergeCandidates|FullyQualifiedName~Integration.Transactions.InvestmentTransactionsEndpoints|FullyQualifiedName~Integration.Transactions.PatchTransaction|FullyQualifiedName~Integration.Transactions.BulkTransactions|FullyQualifiedName~Integration.Transactions.BalanceMergeHideSync|FullyQualifiedName~Integration.Transactions.InKindTransfer'

SHARD_FILTER_s2='FullyQualifiedName~Integration.Auth|FullyQualifiedName~Integration.Accounts|FullyQualifiedName~Integration.Meta'

SHARD_FILTER_s3='FullyQualifiedName~Integration.FeedConnections|FullyQualifiedName~Integration.Backup|FullyQualifiedName~Integration.Reporting|FullyQualifiedName~Integration.Mcp|FullyQualifiedName~Integration.Ingest'

SHARD_FILTER_s4='FullyQualifiedName!~Integration.Transactions&FullyQualifiedName!~Integration.Auth&FullyQualifiedName!~Integration.Accounts&FullyQualifiedName!~Integration.Meta&FullyQualifiedName!~Integration.FeedConnections&FullyQualifiedName!~Integration.Backup&FullyQualifiedName!~Integration.Reporting&FullyQualifiedName!~Integration.Mcp&FullyQualifiedName!~Integration.Ingest&FullyQualifiedName!~Integration.Stress'

