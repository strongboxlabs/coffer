# 0052 — Moneydance re-import idempotency (MD `txn.Id` is not stable)

* Status: Accepted — `prune-batch` remediation (D1), soft-delete clears
  `needs_review` (D3), manual-merge audit + `is_merge_winner` reconcile (D4),
  and the seed-once importer mechanism (D2) all implemented.
* Date: 2026-06-17
* Related: ADR-0050 (import-once / D10 — MD seeds once, then Coffer owns),
  ADR-0022 (`external_id` at the header level), ADR-0034 (`created_at`
  immutable; balances via explicit recompute, not triggers), ADR-0032 (holdings
  recompute at call sites), ADR-0027 (feed → action mapping).

## Context

After re-importing a refreshed Moneydance export into the **Default** (real-world)
ledger, previously **hidden / merged / reconciled** transactions reappeared as
visible duplicates, and account balances were inflated. The user reported
"nothing new was reimported" and a SimpleFIN sync that "found no new
transactions" — yet duplicates appeared.

The re-imports had been run against a real-world ledger to exercise other features
(next-due, parity); testing re-imports belong in the **Demo** ledger, never
Default. That operational mistake is the trigger, but it exposed a latent
correctness gap in the importer worth recording.

## Root cause (evidence-backed)

The importer keys every transaction by Moneydance's `txn.Id`
(`TransactionMapper` → `ExternalId: txn.Id`; the investment path strips the
`:leg` suffix to the same id). Idempotency rests entirely on that id being
stable across exports. **It is not.**

`created_at` is preserved on conflict and immutable post-insert (ADR-0034), so
it is a faithful record of when a row was first written. The histogram showed:

* the seed import created tens of thousands of transactions in one batch;
* a later re-import of a newer export created a **batch of brand-new rows**
  (register rows + reminder templates) — genuine INSERTs, not idempotent
  updates.

Those rows were **not** new data: their posted dates fell entirely inside the
seed's existing coverage. They INSERTed (rather than updated) because their
`external_id` (`txn.Id`) **differed** from the seed's id for the same logical
transaction. Most of the affected register rows were **`online_import`** with OFX
fitids — versus a minority in the seed baseline — confirming the user's hypothesis:
**Moneydance reassigns a transaction's internal id when it merges a register
entry with an online (downloaded) transaction.** File-import and manual rows
re-keyed too, so online-merge is the dominant but not the only trigger.

Because the upsert is `ON CONFLICT (ledger_id, external_id) DO UPDATE`, a changed
id misses the conflict and INSERTs a fresh header — and the insert path hard-sets
`is_hidden = false` and `is_merged_into = null`. **That is the resurrection:**
whatever the user had hidden or merged comes back as a visible, unmerged
duplicate, double-counting in balances.

The seed rows carry **no fitid** (captured by a later importer version), so the
existing `uq_txn_headers_online_match` partial-unique index could not catch the
re-keyed online rows against their seed twins.

## D1 — Remediation: `prune-batch` (implemented, Accepted)

A new importer subcommand, `coffer-import-moneydance prune-batch`, surgically
removes a transaction batch identified by `(ledger_id, import_source,
created_at window)` and re-derives the affected balances + holdings.

* **Dry-run by default.** It lists every header it would delete and classifies
  each by whether a pre-batch row still covers the same `(account, posted-date,
  amount)` — a "twin" the delete falls back to — and flags any row **without** a
  twin (deleting it would lose a transaction). `--apply` performs the delete +
  recompute in one transaction.
* **Recompute mirrors the API's terminal-commit recipe** (the Dapper path
  bypasses the EF interceptors, so it is invoked explicitly, exactly as
  `BalanceRecomputeStep` / `HoldingsRecomputeService` do): per affected
  (holdings-account, security) pair `recompute_holdings_for_account_security`,
  and per affected account `fn_recompute_balances_for_account(…, '0001-01-01')`.
* **FK cascades** clean legs → lots, overrides, tags, balances, sync-promotions;
  recurring-template headers are kept (their `recurring_transactions` parent is
  `RESTRICT`, and they are the user's reminders).
* **Scope guard:** every statement is constrained by `ledger_id`.

Logic lives in `PruneImportBatch` (testable against the migrated schema);
`PruneImportBatchCommand` only parses options and renders. Covered by an
integration test (`PruneImportBatchTests`): seed → inject a re-keyed batch
(twin dup + no-twin row + template) → plan (asserts twin classification +
template exclusion) → apply (asserts rows gone, legs cascaded, template + prior
preserved, balance re-derived from inflated to correct).

**Applied to Default on 2026-06-17:** the register rows removed, the reminder
templates kept, affected balances + holding pairs recomputed. Verified: batch gone,
the duplicate month-end interest on a checking account collapsed to the single
original feed row, holdings non-negative/non-null, no orphan lots, feed/seed/
manual rows untouched. A `pg_dump` safety net was taken first.

This tool is a reusable "undo a bad import batch", not a one-off.

## D2 — Seed-once: the import seeds a fresh ledger, once (Accepted, implemented)

`txn.Id` is not a cross-export idempotency key — MD re-keys it on online-merge,
which is the whole reason a re-import resurrected hidden/merged rows as
duplicates. The decision: **the Moneydance import is a one-time SEED of a fresh
ledger — not a re-import or a sync.** There is no other use for an MD export
file; ongoing data comes from the live feed (SimpleFIN) and manual entry.

**Guard.** If the target ledger already holds any transactions, the importer
refuses (exit non-zero, nothing written) and tells the operator to create a new
ledger or wipe this one first. There is deliberately no "insert new, skip
existing" mode: MD's re-keying makes "new vs. already-imported" undecidable
without a stable key, so a partial re-import can't be done safely.
`ImportCommand` runs the check right after it resolves the ledger
(`TransactionsRepository.CountTransactionHeadersAsync`).

**Simplification (same change).** Because the importer now only ever writes into
an empty ledger, all re-import idempotency machinery is dead and is removed in
the same change — leaving plain inserts:

* `TransactionsRepository.BulkUpsertAsync`: header `ON CONFLICT … DO UPDATE` →
  plain `INSERT`; the leg DELETE-then-INSERT → plain `INSERT`.
* `AccountsRepository.UpsertWithAdoptionAsync`: the same-name *adoption* path is
  dropped (insert + write the junction row).
* `LoanTermsRepository` / `RecurringTransactionsRepository`: `ON CONFLICT …` →
  plain `INSERT`.

The within-export FITID dedup stays — it's data-quality for a single export, not
re-import idempotency. Note the old `ON CONFLICT` upsert was *false* idempotency:
it caught same-`external_id` re-imports but not the re-keyed ones — exactly how
the resurrection bug slipped through. The guard is the real safety; the upsert
was both dead and misleading.

**Demo / re-seed.** The Demo refresh wipes first, leaving the ledger empty, so it
re-seeds normally. To re-seed any ledger: wipe it (`prune-batch`) or use a fresh
one.

**Deferred — (C) fitid hardening.** Back-filling OFX fitids so
`uq_txn_headers_online_match` rejects online re-keys is unnecessary under total
refuse, and would not help the SimpleFIN feed anyway (different id namespace).
Revisit only if an OFX-file import path ever needs it.

**Rejected — (B) natural-key dedup.** Matching on account/date/amount/payee is
fuzzy for recurring same-amount charges and merge-shifted dates — it would drop
real new records or re-introduce duplicates.

## D3 — Soft-delete clears `needs_review` (implemented, Accepted)

A second anomaly surfaced while untangling the user's "to-be-accepted queue":
**dozens of feed rows were `is_hidden=true` AND `needs_review=true`** — invisible in the
register (the resolved view filters `is_hidden`) yet still counted in the review
tab. Root-caused: the **only** writer of `is_hidden=true` is the soft-delete path
(`TransactionsRepository.DeleteAsync`, `InvestmentTransactionsRepository`,
`BulkTransactionsRepository` — confirmed by grep across `src/` and `db/`; no
migration/trigger/ingest path sets it). Per ADR-0023 / migration 117, deleting a
row that carries an `external_id` (any feed/import row) **soft-hides** it instead
of hard-deleting, to keep re-source idempotent. But the soft-delete left
`needs_review` set — so deleting a not-yet-accepted row stranded it as
hidden-but-pending: gone from the register, still in the review queue, with no UI
to resolve it.

Decision: **every soft-delete branch now also sets `needs_review = false`.** A
deleted row is resolved, not awaiting acceptance. Covered by the delete
integration tests (single + bulk: a feed row seeded `needs_review=true` is
asserted cleared after delete).

Note (observability gap, not yet fixed): there is **no audit trail for
`is_hidden` flips** — `txn_headers` has no `updated_at`, `track_commit_timestamp`
is off, and `sync_run_promotions` records promotions only — so "who/when a row
was soft-deleted" is unrecoverable. Worth adding a `hidden_at`/actor or enabling
commit-timestamp tracking.

The pre-existing stranded rows are a one-time data cleanup (clear their
`needs_review`), handled separately from this code change.

## D4 — Manual-merge audit + `is_merge_winner` reconcile (implemented, Accepted)

Investigating a cross-source pair (a small deposit visible twice on one bank
account) raised the **inverse** of D1's concern: could the *merge* path have
**over-merged** — collapsed two genuinely distinct transactions into one and
understated a balance? Full audit of **every merge** on the Default ledger:

**Mechanism — merging is 100% manual; nothing auto-merges.** The only writer of
`is_merged_into` is `TransactionsRepository.PatchAsync` (runs only when the SPA
sends an explicit `MergeFromHeaderId`). Ingest (`IngestOrchestrator`) *reads*
`is_merged_into` as a dedup guard but only **inserts** new feed rows or **skips**
already-known ones by `external_id` — it never updates an existing row or creates
a merge. The MD importer always writes `is_merged_into=null` on fresh rows. So a
merge never happens behind the user's back; an over-merge can only be a deliberate
(mis)action, bounding the blast radius accordingly.

**No balance errors found.**

* Nearly all merges are same-magnitude (true dedups / count-neutral folds).
* The handful of magnitude-changing merges are all **paychecks** and net-identical on the
  cash account, by workflow: the user's provisional **scheduled-transaction**
  entry is edited to match the actual deposit (so the online row surfaces a merge
  candidate), then post-merge split tweaks (employer match, withholding)
  rebalance *within* the gross — the net to the bank account is unchanged
  (verified leg-by-leg: the feed's net-pay deposit folds into the gross paycheck
  whose bank-account legs net to exactly that same figure).
* **Zero investment merges exist** — every merge is bank/credit. The case where a
  share/amount mismatch *would* corrupt holdings (investment merges must match the
  brokerage actuals) has **no instances**; investment rows reach final state via
  accept/un-hide, not merge.

**Detector limit (recorded so we do not re-alarm).** A count-reconciliation check
(visible rows vs. the max distinct `external_id` any one feed reported, per
account/date/net) flagged several clusters as "under-counted." All were **false
positives**: feeds legitimately assign one real transaction **multiple ids** —
SimpleFIN reports each *side* of an internal transfer as its own transaction (e.g.
a $1,234.56 internal transfer between two of the user's own accounts arrived as
1 MD + 2 SimpleFIN ids and was correctly folded to one transfer), plus re-syncs and pending→posted re-keys, plus merges that cross a
date boundary. Consequently **a genuine over-merge of two distinct same-amount
transactions is not detectable from the data alone** — only statement
reconciliation catches it (exactly how the user surfaced the $50.00 discrepancy). "0 drift"
cannot catch it either: an over-merge lowers the walked and stored balance
equally, so they still agree; only the statement disagrees.

**`is_merge_winner` drift (a few rows) — reconciled (mig 128).** A few visible SimpleFIN
card-payee rows are merge targets yet carried
`is_merge_winner=false`: residue from the pre-"merge-direction-invert"
code (winner/loser reversed) on rows created after mig 107's backfill
ran. `is_merged_into` (the truth) is correct — only the denormalized overlay flag
was stale, so balances/register were never affected. Migration 128 reconciles the
denorm (sets missing TRUEs from the pointers; idempotent; does not clear monotonic
TRUEs). The active call-site keeps it correct going forward; if drift ever recurs,
harden via a trigger on `is_merged_into` changes or compute the flag in-view.

**Residual provenance tangles (no balance impact, left as-is):** some chained merges
(A→B→C) and same-provider folds. The resolved view always lands on the final
survivor (`is_merged_into IS NULL`), so balances are correct, but a one-hop
"merged into X" pointer can target a row that is itself merged. **There is no
unmerge surface today** (mig 107: the flag is monotonic) — undoing a fold needs
either an unmerge feature or a surgical correction.

**Reconciliation aid (worksheet).** Because statement reconciliation is the only
thing that catches a genuine over-merge, this query makes it a glance — per
account, every visible row that absorbed a merge, with its folded-in sources:

```sql
SELECT w.posted_at::date AS date, w.payee,
       (SELECT SUM(amount) FROM txn_legs WHERE header_id = w.id AND account_id = a.id) AS amount_on_account,
       COALESCE(w.provider_key, 'manual') AS surviving_source,
       (SELECT count(*) FROM txn_headers l WHERE l.is_merged_into = w.id) AS folded_in,
       (SELECT string_agg(COALESCE(l.provider_key,'manual') || ' ' || left(COALESCE(l.external_id,'manual'),16), ', ')
          FROM txn_headers l WHERE l.is_merged_into = w.id) AS folded_sources
  FROM txn_headers w
  JOIN txn_legs wl ON wl.header_id = w.id
  JOIN accounts a  ON a.id = wl.account_id
 WHERE a.ledger_id = :ledger_id AND a.name = :account_name
   AND w.is_merged_into IS NULL AND NOT w.is_hidden
   AND EXISTS (SELECT 1 FROM txn_headers l WHERE l.is_merged_into = w.id)
 GROUP BY w.id, w.posted_at, w.payee, w.provider_key, a.id
 ORDER BY date;
```

Can be promoted to a `ledger-import-moneydance merge-audit --account` subcommand
if a standing tool is wanted.

## Consequences

* The ledger is restored to its pre-re-import state; reconciliation
  (hidden / merged) preserved.
* A durable, tested tool exists to undo any future bad import batch.
* Until D2 lands, the operational rule stands: **never re-import the Default
  ledger**; testing imports go to Demo.
* The merge path (D4) is sound: manual-only, no balance error across every merge,
  no investment merges. The only undetectable risk — a deliberate over-merge of
  two distinct same-amount transactions — is bounded and surfaces only against a
  statement; the worksheet query makes that check fast.
* Open: agree D2 and implement it (its own slice). Optional future hardening
  (not scheduled): an unmerge surface; a soft warning on same-source folds; and,
  if `is_merge_winner` drifts again, move it to a trigger-maintained or in-view
  computation.
