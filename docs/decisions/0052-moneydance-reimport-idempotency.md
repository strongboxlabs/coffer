# 0052 — Moneydance re-import idempotency (MD `txn.Id` is not stable)

* Status: Accepted — `prune-batch` remediation (D1), soft-delete clears
  `needs_review` (D3), manual-merge audit + `is_merge_winner` reconcile (D4),
  and the seed-once importer mechanism (D2) all implemented. *Amended
  2026-10-25:* `prune-batch` is removed (see the amendment at the end).
* Date: 2026-06-17
* Related: ADR-0050 (import-once / D10 — MD seeds once, then Coffer owns),
  ADR-0022 (`external_id` at the header level), ADR-0034 (`created_at`
  immutable; balances via explicit recompute, not triggers), ADR-0032 (holdings
  recompute at call sites), ADR-0027 (feed → action mapping).

## Context

Re-importing a newer Moneydance export into a ledger that is already seeded and
in use brings previously **hidden / merged / reconciled** transactions back as
visible duplicates and inflates account balances — even when the export holds
nothing new.

Re-imports for testing belong in the **Demo** ledger, never in a ledger in use.
That rule does not make the importer correct: the duplication is a correctness
gap in the importer itself.

## Root cause

The importer keys every transaction by Moneydance's `txn.Id`
(`TransactionMapper` → `ExternalId: txn.Id`; the investment path strips the
`:leg` suffix to the same id). Idempotency rests entirely on that id being
stable across exports. **It is not.**

`created_at` is preserved on conflict and immutable post-insert (ADR-0034), so
it is a faithful record of when a row was first written, which makes the fault
visible:

* the seed import creates the whole transaction history in one batch;
* a later re-import of a newer export can create a **batch of brand-new rows**
  (register rows + reminder templates) — genuine INSERTs, not idempotent
  updates.

Those rows are **not** new data: their posted dates fall inside the seed's
existing coverage. They INSERT (rather than update) because their
`external_id` (`txn.Id`) **differs** from the seed's id for the same logical
transaction:
**Moneydance reassigns a transaction's internal id when it merges a register
entry with an online (downloaded) transaction.** File-import and manual rows
can re-key too, so online-merge is not the only trigger.

Because the upsert is `ON CONFLICT (ledger_id, external_id) DO UPDATE`, a changed
id misses the conflict and INSERTs a fresh header — and the insert path hard-sets
`is_hidden = false` and `is_merged_into = null`. **That is the resurrection:**
whatever the user had hidden or merged comes back as a visible, unmerged
duplicate, double-counting in balances.

Rows seeded by an importer version that predates fitid capture carry **no
fitid**, so the existing `uq_txn_headers_online_match` partial-unique index
cannot catch re-keyed online rows against their seed twins.

## D1 — Remediation: `prune-batch` (implemented, Accepted)

> *Amended 2026-10-25: removed.* D2 made the batch this command undoes
> unreachable; see the amendment at the end.

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

Take a `pg_dump` before `--apply`: the delete is a hard delete with cascades, so
nothing inside Coffer can bring a pruned row back.

This tool is a reusable "undo a bad import batch", not a one-off.

## D2 — Seed-once: the import seeds a fresh ledger, once (Accepted, implemented)

`txn.Id` is not a cross-export idempotency key — MD re-keys it on online-merge,
which is the whole reason a re-import resurrected hidden/merged rows as
duplicates. The decision: **the Moneydance import is a one-time SEED of a fresh
ledger — not a re-import or a sync.** There is no other use for an MD export
file; ongoing data comes from the live feed (SimpleFIN) and manual entry.

**Guard.** If the target ledger already holds any transactions, the importer
refuses (nothing written) and tells the operator to create a new ledger. There is deliberately no "insert new, skip
existing" mode: MD's re-keying makes "new vs. already-imported" undecidable
without a stable key, so a partial re-import can't be done safely.
`MoneydanceImportService` runs the check right after it resolves the ledger
(`TransactionsRepository.CountTransactionHeadersAsync`). *Amended 2026-10-25:* the
import now always creates its ledger, so the check is replaced by that (see the
amendment at the end).

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
it caught same-`external_id` re-imports but not the re-keyed ones, which are the
ones that resurrect rows. The guard is the real safety; the upsert
was both dead and misleading.

**Demo / re-seed.** The Demo refresh wipes first, leaving the ledger empty, so it
re-seeds normally. To re-seed any other ledger, import into a fresh one.
*Amended 2026-10-25:* no Demo refresh was built; demo provisioning creates a new
Demo ledger like any import, and re-seeding is always an import into a new ledger.

**Deferred — (C) fitid hardening.** Back-filling OFX fitids so
`uq_txn_headers_online_match` rejects online re-keys is unnecessary under total
refuse, and would not help the SimpleFIN feed anyway (different id namespace).
Revisit only if an OFX-file import path ever needs it.

**Rejected — (B) natural-key dedup.** Matching on account/date/amount/payee is
fuzzy for recurring same-amount charges and merge-shifted dates — it would drop
real new records or re-introduce duplicates.

## D3 — Soft-delete clears `needs_review` (implemented, Accepted)

A second defect sits in the review queue:
**feed rows that are `is_hidden=true` AND `needs_review=true`** — invisible in the
register (the resolved view filters `is_hidden`) yet still counted in the review
tab. The **only** writer of `is_hidden=true` is the soft-delete path
(`TransactionsRepository.DeleteAsync`, `InvestmentTransactionsRepository`,
`BulkTransactionsRepository`; no migration/trigger/ingest path sets it). Per
ADR-0023 / migration 117, deleting a row that carries an `external_id` (any
feed/import row) **soft-hides** it instead of hard-deleting, to keep re-source
idempotent. A soft-delete that leaves `needs_review` set strands a
not-yet-accepted row as hidden-but-pending: gone from the register, still in the
review queue, with no UI to resolve it.

Decision: **every soft-delete branch now also sets `needs_review = false`.** A
deleted row is resolved, not awaiting acceptance. Covered by the delete
integration tests (single + bulk: a feed row seeded `needs_review=true` is
asserted cleared after delete).

Note (observability gap, not yet fixed): there is **no audit trail for
`is_hidden` flips** — `txn_headers` has no `updated_at`, `track_commit_timestamp`
is off, and `sync_run_promotions` records promotions only — so "who/when a row
was soft-deleted" is unrecoverable. Worth adding a `hidden_at`/actor or enabling
commit-timestamp tracking.

The fix is forward-only. A row soft-deleted before it keeps `needs_review = true`:
neither the code change nor any migration clears it, so clearing one is a separate
data fix.

## D4 — Manual-merge audit + `is_merge_winner` reconcile (implemented, Accepted)

D1's concern has an **inverse**: can the *merge* path **over-merge** — collapse
two genuinely distinct transactions into one and understate a balance? An audit
of the merge path:

**Mechanism — merging is 100% manual; nothing auto-merges.** The only writers of
`is_merged_into` are `TransactionsRepository.PatchAsync` and its investment twin
(each runs a merge only when the merge route, `POST …/{headerId}/merge`, passes the
candidate the user picked as `MergeFromHeaderId`; a PATCH body cannot carry it).
Ingest (`IngestOrchestrator`) *reads*
`is_merged_into` as a dedup guard but only **inserts** new feed rows or **skips**
already-known ones by `external_id` — it never updates an existing row or creates
a merge. The MD importer always writes `is_merged_into=null` on fresh rows. So a
merge never happens behind the user's back; an over-merge can only be a deliberate
(mis)action, bounding the blast radius accordingly.

**What a merge can do to a balance.**

* A same-magnitude merge (a true dedup, a count-neutral fold) changes no balance.
* A magnitude-changing merge is net-identical on the cash account when it follows
  the scheduled-transaction workflow: a provisional **scheduled-transaction**
  entry is edited to match the actual deposit (so the online row surfaces a merge
  candidate), then post-merge split tweaks (on a paycheck, say: employer match,
  withholding) rebalance *within* the gross — the net to the bank account is
  unchanged, because the feed's net deposit folds into a header whose bank-account
  legs net to exactly that same figure.
* An investment merge is the case where a share/amount mismatch *would* corrupt
  holdings (it must match the brokerage actuals); investment rows normally reach
  their final state via accept/un-hide, not merge.

**Detector limit.** A count-reconciliation check
(visible rows vs. the max distinct `external_id` any one feed reported, per
account/date/net) would flag clusters as "under-counted" that are **false
positives**: feeds legitimately assign one real transaction **multiple ids** —
SimpleFIN reports each *side* of an internal transfer as its own transaction (so
an internal transfer between two of the user's own accounts can arrive as one MD id
plus two SimpleFIN ids, correctly folded to one transfer), plus re-syncs and pending→posted re-keys, plus merges that cross a
date boundary. Consequently **a genuine over-merge of two distinct same-amount
transactions is not detectable from the data alone** — only statement
reconciliation catches it. "0 drift"
cannot catch it either: an over-merge lowers the walked and stored balance
equally, so they still agree; only the statement disagrees.

**`is_merge_winner` drift — reconciled (mig 128).** A visible row could be a merge
target yet carry `is_merge_winner=false`: residue from the pre-"merge-direction-invert"
code (winner/loser reversed) on rows created after mig 107's backfill
ran. `is_merged_into` (the truth) is correct — only the denormalized overlay flag
was stale, so balances/register were never affected. Migration 128 reconciles the
denorm (sets missing TRUEs from the pointers; idempotent; does not clear monotonic
TRUEs). The active call-site keeps it correct going forward; if drift ever recurs,
harden via a trigger on `is_merged_into` changes or compute the flag in-view.

**Residual provenance tangles (no balance impact, left as-is):** chained merges
(A→B→C) and same-provider folds can exist. The resolved view always lands on the final
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

* `prune-batch` returned a ledger to its pre-re-import state, with the
  reconciliation (hidden / merged) on its surviving rows preserved. It is removed
  (amendment, 2026-10-25).
* D2 makes the operational rule structural: the importer refuses any ledger that
  already holds transactions, so a ledger in use cannot be re-imported; testing
  imports go to Demo. *Amended 2026-10-25:* every import creates its own ledger, so
  there is no ledger to refuse.
* The merge path (D4) is sound by construction: manual-only, so nothing merges
  behind the user's back. The only undetectable risk — a deliberate over-merge of
  two distinct same-amount transactions — is bounded and surfaces only against a
  statement; the worksheet query makes that check fast.
* Optional future hardening
  (not scheduled): an unmerge surface; a soft warning on same-source folds; and,
  if `is_merge_winner` drifts again, move it to a trigger-maintained or in-view
  computation.

## Amendment, 2026-10-25 — `prune-batch` is removed

`prune-batch` undid a batch of rows that a re-import inserted into a ledger already
in use. Under D2 no import writes into a ledger that holds transactions, so no such
batch can be created, and the command was a hard delete run from a source checkout
straight against the database, outside the API's permissions and audit. It is removed
with its data logic (`PruneImportBatch`) and its test.

The import itself now runs only in the app, which creates a new ledger each time
(ADR-0071, amendment of this date). The command-line tool keeps the two read-only
diagnostics, `audit` and `reconcile`.

**D2's guard becomes structural.** With the command-line `--ledger-id` gone, the
import's only way to an existing ledger was the name typed for the new one: the
service reused any ledger already carrying that name. It runs as `coffer_service`,
which bypasses row-level security, so the name matched other users' ledgers too: an
import could write into a ledger its user cannot see, without a grant, or be refused
with a message giving that ledger's transaction count. The service now takes only a
name and always creates the ledger, with the owner grant, in the transaction that
fills it; the name is a label and is never looked up. No ledger it writes can hold
earlier rows, so the transaction-count check and its refusal are removed rather than
kept unreachable. A test imports under a name already held by another user's empty
ledger, then by a populated one and by two at once, and requires a new ledger, owned
by the importer alone, each time.
