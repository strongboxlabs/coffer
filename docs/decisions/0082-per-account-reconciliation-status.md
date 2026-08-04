# ADR-0082 — Per-account reconciliation status

**Status:** Accepted (Option B; four decisions resolved below; implementation in slices)

## Context

Reconciliation status (`uncleared` / `reconciling` / `cleared`) is stored **per
transaction header**: `txn_headers.status`, plus `cleared_at` +
`cleared_by_user_id` and a CHECK tying `status='cleared'` ⇔ `cleared_at IS NOT
NULL` (migration 030). It surfaces through `resolved_transactions.status`, the
register's status column (`resolveRowStatus`), the single recon endpoint
(`SetReconStatusAsync` → `PUT …/{headerId}/recon-status`), and the bulk endpoint
(`BulkSetReconStatusAsync` → `POST …/bulk-recon-status`).

**The problem.** A transaction touches more than one account (its legs). You
reconcile each *account* independently against its own statement — a transfer
from Checking to Savings can be **cleared in Checking** while still
**uncleared in Savings** (the money left one bank before it landed in the
other). With one header-level status, clearing the transfer in Checking also
marks it cleared in Savings, which is wrong. This is standard behaviour in
Moneydance/Quicken: reconciliation is a per-account activity.

So reconciliation status must move from the header to the **account side of each
leg**.

## Decision drivers

- **Correctness:** each real account's cleared/uncleared state must be
  independent for the same transaction.
- **ADR-0003 (immutable feed + overrides):** raw feed rows (`txn_headers`,
  `txn_legs`) are immutable; user actions live in an overlay. Clearing a row is
  a user action, so its state belongs in the overlay layer, not on raw legs.
- **Scope of "account":** you reconcile **real accounts** (bank / credit / cash
  / asset / liability / loan) against statements. **Categories are not
  reconciled.** So per-account status applies to the *real-account* legs of a
  transaction; category legs have no meaningful recon status.
- **Register read is hot** (ADR-0034 keyset): the per-account status must be
  cheap to read in the register query.

## Options

### A. Move `status`/`cleared_at`/`cleared_by` onto `txn_legs`
Add the columns to the raw leg table, one status per leg.
- ✅ Simple read (status is right there on the leg).
- ❌ Violates ADR-0003 — raw feed legs are immutable; a user clearing action
  would mutate the feed row. Re-import/idempotency reasoning (ADR-0052) assumes
  raw legs don't carry user state.

### B. Per-leg recon overlay (RECOMMENDED)
A dedicated overlay table `txn_leg_recon` keyed by `leg_id` (or
`(header_id, account_id)`), holding `status` + `cleared_at` + `cleared_by_user_id`
+ the consistency CHECK. Absent row ⇒ `uncleared` (the default). Only
real-account legs ever get a row.
- ✅ Consistent with ADR-0003 (user state in an overlay, raw legs untouched).
- ✅ Independent per account.
- ✅ Re-import leaves recon state intact (keyed off the surviving leg).
- ⚠️ Register read joins one more small table (LEFT JOIN, COALESCE default).

### C. Statement-based reconciliation
A full reconcile-against-statement flow (statement periods, opening/closing
balances, a reconcile session that flips legs cleared in bulk). MD's "Reconcile"
window.
- ✅ The complete accountant workflow.
- ❌ Much larger; a superset of B. B is the necessary substrate for C anyway
  (C needs per-account cleared state to exist first).

## Proposed decision

**Option B** — a per-leg recon overlay — as the foundation. It's the minimal
correct model and the prerequisite for C later. C (statement reconciliation) is
**out of scope** for this ADR; if wanted, it's a follow-up built on B.

## Design (Option B)

### Schema (new migration)
- New table `txn_leg_recon`:
  - `leg_id UUID PK REFERENCES txn_legs(id) ON DELETE CASCADE`
  - `ledger_id UUID NOT NULL` (RLS + composite FK, per existing convention)
  - `status TEXT NOT NULL DEFAULT 'uncleared' CHECK (status IN ('uncleared','reconciling','cleared'))`
  - `cleared_at TIMESTAMPTZ`, `cleared_by_user_id UUID REFERENCES users(id) ON DELETE SET NULL`
  - `CHECK ((status='cleared') = (cleared_at IS NOT NULL))`
- **Backfill:** for every existing header with `status <> 'uncleared'`, insert a
  `txn_leg_recon` row for each of its **real-account** legs, copying
  `status`/`cleared_at`/`cleared_by_user_id` from the header. (Header-level state
  fans out to each account side; the pre-per-account world had them all equal
  anyway.)
- **Then** drop `txn_headers.status`, `cleared_at`, `cleared_by_user_id` and
  their two CHECKs.

### Resolved view + register
- `resolved_transactions.status` becomes the **per-leg** status: LEFT JOIN
  `txn_leg_recon` on `leg_id`, `COALESCE(status,'uncleared')`. Each resolved row
  is already leg-scoped and account-scoped, so a transfer yields two rows with
  independent statuses — exactly the register's per-account view.
- `resolveRowStatus` (client) is unchanged in shape — it still reads
  `row.status`; that value is now the account's status.

### Endpoints
- `PUT …/transactions/{headerId}/recon-status` gains an **account scope**: it
  sets the status on the leg for the account whose register is calling — the
  `accountId` travels in the request **body** (decision 1 below); the register
  already knows its account.
- `POST …/transactions/bulk-recon-status`: the selection is already
  account-scoped (`SelectionRequest.AccountId`), so bulk-clear targets the legs
  on that account. Upsert `txn_leg_recon` per selected leg.
- The `cleared_at`/`cleared_by` audit moves to the overlay upsert.

### Recompute
Recon status is **not** balance-affecting (it never was — migration 030 note),
so no balance recompute is involved. Purely a display/audit dimension.

## Merge → `reconciling` rule

When a fresh bank-feed row is **merged** into an existing transaction (ADR-0072
merge flow), the feed match is the bank acknowledging that transaction. On the
**merge account** (the source account the import row and the surviving candidate
share), the survivor's recon status is bumped to `reconciling` — **unless it is
already `cleared`** (the stronger, user-affirmed state wins). On the survivor's
leg for that account:

- `uncleared` → `reconciling`
- `reconciling` → `reconciling` (no-op)
- `cleared` → `cleared` (unchanged)

Only the merge account's leg is affected — the other side of a transfer hasn't
been bank-confirmed, so its status is untouched. Implemented in the merge block
of `PatchAsync` (alongside the existing import-date adoption + merge stamping) by
upserting the winner leg's `txn_leg_recon` row.

## Decisions (resolved)

1. **Single-toggle endpoint shape:** `accountId` in the **body**, not the path —
   least disruptive, and the register already knows its account.
2. **Overlay key:** `leg_id` — each leg's status is independent (a same-account
   split's legs don't collapse).
3. **Vocabulary:** keep the 3-state `uncleared` / `reconciling` / `cleared`
   unchanged — avoids a second migration when statement-reconcile (Option C)
   lands, and the merge rule above uses `reconciling` as a meaningful middle
   state today.
4. **Real-account legs only:** category legs never get a recon row (resolve to
   `uncleared` / N/A); the register renders no status affordance on the category
   side.

## Consequences

- A schema migration that drops three `txn_headers` columns + two CHECKs and
  adds one overlay table — touches the resolved view, both recon endpoints, the
  register read, and the reimport/dedup reasoning (verify ADR-0052 queries that
  reference header status, if any).
- Reconciling a transfer behaves correctly per account.
- Sets up (does not build) a future statement-reconciliation flow (Option C).
- Tests: per-account independence (transfer cleared on one side only), backfill
  correctness, bulk-recon per account, the consistency CHECK on the overlay.
