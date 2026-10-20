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
# -----------------------------------------------------------------------------
# THE PARTITION, AND THE MEASUREMENTS BEHIND IT (2026-09-29)
# -----------------------------------------------------------------------------
# The shards run in PARALLEL inside one job, so the job costs what the SLOWEST
# shard costs. Balancing them is the whole game; total test time is irrelevant.
#
# The previous six-way split had a critical path of 1131s in CI while one shard
# (importer) finished in 25s and then held an idle slot for eighteen minutes.
# Namespace costs, measured serially in Release on an otherwise quiet machine:
#
#   Transactions   749s/353   FeedConnections 168s/53   Accounts 141s/129
#   Backup         128s/90    Reminders       121s/75   Auth     107s/106
#   Ingest          93s/61    Notifications    48s/64   Reporting 43s/145
#   ...18 others   297s/245                     TOTAL ~1930s
#
# Two things that map says:
#
#   * TEST COUNT IS NOT COST. FeedConnections is 3.2s/test; Reporting is 0.3s.
#     A tenfold spread, which is how the old partition drifted — it looks
#     balanced by test count and is not.
#
#   * Transactions alone is 39% of all work. No regrouping of the other
#     namespaces can fix the critical path while Transactions sits in two
#     pieces, because its larger piece (~459s, from the s1:s1b ratio CI
#     measured concurrently) already exceeds an even share of the total.
#     Hence three pieces.
#
# Target: ~240s per shard over eight slots. The eight-way split is deliberate
# and has a PREREQUISITE — eight test processes plus eight Testcontainers
# Postgres instances do not fit in the WSL runner's old 12 GB budget. Landing
# this without the .wslconfig bump to 20 GB would make CI slower, not faster.
#
# -----------------------------------------------------------------------------
# THE COMPLEMENT PROPERTY — do not break this
# -----------------------------------------------------------------------------
# `rest` is the complement of every other shard: it excludes by name what they
# include, so a newly added Integration/<Folder> falls into it AUTOMATICALLY
# and can never be silently skipped. Adding a namespace to a named shard means
# adding it to `rest`'s exclusions in the same edit.
#
# `trx_c` plays the same role inside Transactions: it is Transactions minus the
# prefixes trx_a and trx_b name, so a new Transactions test class lands there
# rather than nowhere.
#
# ONE DELIBERATE CARVE-OUT: `rest` ALSO excludes Integration.Stress, which no
# shard includes — so those tests run in no shard at all. That is intended.
# They are the cost/performance boundary tests and they have their own
# dedicated lane, deliberately outside the gate.
#
# VERIFY, DON'T TRUST. After ANY edit here, prove the union of these filters is
# every test exactly once — list the tests each filter selects and diff that
# against the full suite. A typo or a forgotten exclusion drops tests from every
# shard, and the run still reports green, which is the one thing a test gate
# must never do. The maintainer gate runs that check automatically.
# =============================================================================

# --- Transactions, three ways (~250s each) -----------------------------------

# The six heavy class groups that were already carved out as s1b.
SHARD_FILTER_trx_a='FullyQualifiedName~Integration.Transactions.MergeCandidates|FullyQualifiedName~Integration.Transactions.InvestmentTransactionsEndpoints|FullyQualifiedName~Integration.Transactions.PatchTransaction|FullyQualifiedName~Integration.Transactions.BulkTransactions|FullyQualifiedName~Integration.Transactions.BalanceMergeHideSync|FullyQualifiedName~Integration.Transactions.InKindTransfer'

# The register/investment/balance families, minus anything trx_a already took.
SHARD_FILTER_trx_b='FullyQualifiedName~Integration.Transactions&(FullyQualifiedName~Integration.Transactions.Register|FullyQualifiedName~Integration.Transactions.Investment|FullyQualifiedName~Integration.Transactions.Balance|FullyQualifiedName~Integration.Transactions.Bank|FullyQualifiedName~Integration.Transactions.BulkDelete|FullyQualifiedName~Integration.Transactions.DateMove|FullyQualifiedName~Integration.Transactions.LedgerBalance)&FullyQualifiedName!~Integration.Transactions.InvestmentTransactionsEndpoints&FullyQualifiedName!~Integration.Transactions.BalanceMergeHideSync&FullyQualifiedName!~Integration.Transactions.BulkTransactions'

# Everything else in Transactions — the complement WITHIN the namespace, so a
# new test class here is covered without editing anything.
SHARD_FILTER_trx_c='FullyQualifiedName~Integration.Transactions&FullyQualifiedName!~Integration.Transactions.MergeCandidates&FullyQualifiedName!~Integration.Transactions.InvestmentTransactionsEndpoints&FullyQualifiedName!~Integration.Transactions.PatchTransaction&FullyQualifiedName!~Integration.Transactions.BulkTransactions&FullyQualifiedName!~Integration.Transactions.BalanceMergeHideSync&FullyQualifiedName!~Integration.Transactions.InKindTransfer&FullyQualifiedName!~Integration.Transactions.Register&FullyQualifiedName!~Integration.Transactions.Investment&FullyQualifiedName!~Integration.Transactions.Balance&FullyQualifiedName!~Integration.Transactions.Bank&FullyQualifiedName!~Integration.Transactions.BulkDelete&FullyQualifiedName!~Integration.Transactions.DateMove&FullyQualifiedName!~Integration.Transactions.LedgerBalance'

# --- the rest, grouped to ~230s each -----------------------------------------

# FeedConnections 168 + Snapshots 32 + Ledgers 28
SHARD_FILTER_feeds='FullyQualifiedName~Integration.FeedConnections|FullyQualifiedName~Integration.Snapshots|FullyQualifiedName~Integration.Ledgers'

# Accounts 141 + Notifications 48 + Tags 36
SHARD_FILTER_accounts='FullyQualifiedName~Integration.Accounts|FullyQualifiedName~Integration.Notifications|FullyQualifiedName~Integration.Tags'

# Backup 128 + Reporting 43 + Mcp 42 + Scheduling 25
SHARD_FILTER_backup='FullyQualifiedName~Integration.Backup|FullyQualifiedName~Integration.Reporting|FullyQualifiedName~Integration.Mcp|FullyQualifiedName~Integration.Scheduling'

# Reminders 121 + Ingest 93 + Quotes 23. Ingest carries the importer tests that
# used to be their own 25s shard holding an idle slot.
SHARD_FILTER_ingest='FullyQualifiedName~Integration.Reminders|FullyQualifiedName~Integration.Ingest|FullyQualifiedName~Integration.Quotes|FullyQualifiedName~Integration.Import'

# The complement: Auth (107) plus every small namespace, plus Unit tests, plus
# any namespace nobody has named yet. Stress stays excluded on purpose.
SHARD_FILTER_rest='FullyQualifiedName!~Integration.Transactions&FullyQualifiedName!~Integration.FeedConnections&FullyQualifiedName!~Integration.Snapshots&FullyQualifiedName!~Integration.Ledgers&FullyQualifiedName!~Integration.Accounts&FullyQualifiedName!~Integration.Notifications&FullyQualifiedName!~Integration.Tags&FullyQualifiedName!~Integration.Backup&FullyQualifiedName!~Integration.Reporting&FullyQualifiedName!~Integration.Mcp&FullyQualifiedName!~Integration.Scheduling&FullyQualifiedName!~Integration.Reminders&FullyQualifiedName!~Integration.Ingest&FullyQualifiedName!~Integration.Quotes&FullyQualifiedName!~Integration.Import&FullyQualifiedName!~Integration.Stress'
