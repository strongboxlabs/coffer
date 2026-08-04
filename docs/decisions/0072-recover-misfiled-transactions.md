# 0072 — Recover mis-filed transactions: hidden view, bulk unhide, move-to-account

Status: Accepted
Date: 2026-07-08
Relates: [ADR-0003](0003-immutable-feed-and-overrides.md), [ADR-0022](0022-txn-headers-and-legs.md), [ADR-0024](0024-register-bulk-selection.md), [ADR-0034](0034-header-walk-running-balance.md), [ADR-0068](0068-mcp-write-surface.md)

## Context

An OFX/QIF file was imported into the wrong account. The register "delete" of the
resulting rows is a **soft-hide** (`txn_headers.is_hidden = true`) for feed rows
(ADR-0003), and the OFX dedup is **ledger-wide** by `(ledger, provider_key,
external_id)` and *counts soft-hidden rows* — so re-importing the same file into
the correct account is blocked as "already known." The transactions aren't lost
(hidden under the wrong account), but there's **no supported recovery path**:
you can't see hidden rows, un-delete them, or move them to the right account.

Three capabilities close that, all reusing existing infrastructure. Together they
are the no-SQL recovery for "imported to the wrong account": open the Hidden
view → select → move to the right account → unhide.

## Decisions

### D1 — Hidden view (see soft-hidden rows)

Add **`hidden`** as a register status filter (a tab), alongside
`all|cleared|uncleared|scheduled|needs_review`. It shows *only* the effectively
hidden rows (`COALESCE(override.is_hidden, header.is_hidden, false) = true`,
still `is_merged_into IS NULL`) — a per-account "trash" view.

The register read is currently hard-filtered to `is_hidden = FALSE` in **two**
places — the `register_entry_keys()` keyset function and `RegisterRepository` —
so both are **parameterized by a visibility mode** (default = visible; `hidden`
= hidden-only). `BulkTransactionsRepository.BuildSelectionQuery` gains the
matching `hidden` case so a select-all in the Hidden view scopes to hidden rows.
Rows render with the muted/greyed treatment (mirroring inactive accounts).

### D2 — Unhide (un-delete)

Single-row `POST /api/ledgers/{ledgerId}/transactions/{headerId}/unhide` + bulk
`POST /api/ledgers/{ledgerId}/transactions/bulk-unhide` (a `SelectionRequest`).
Mirrors the delete path's soft-hide, inverted: set `is_hidden = false` (only on
rows currently hidden), then the ADR-0034 balance + holdings recompute for the
affected accounts (same explicit call-site as bulk-delete). Hard-deleted manual
rows are gone and out of scope — unhide only resurrects soft-hidden rows.

### D3 — Move to a different account

Single-row `POST /api/ledgers/{ledgerId}/transactions/{headerId}/move-account`
+ bulk `POST /api/ledgers/{ledgerId}/transactions/bulk-move-account`, body
carries the **target account** (and, for bulk, the `SelectionRequest` whose
`AccountId` is the source scope).

Mechanism: repoint the **source-side leg(s)** — the leg(s) on the *current*
account (`account_type != 'category'`) — to the target, a **direct
`txn_legs.account_id` update** (the same kind of mutation recategorize already
does to the category-side leg, ADR-0068), then recompute both the source and
target accounts. For a split, every source-side leg (one per posting) moves, so
the whole transaction relocates. `external_id`/`provider_key` stay on the header
(the ledger-wide FITID dedup is unchanged — the move fixes placement without a
re-import).

**Guards:**
- **Bank-shape only.** An **investment** transaction (`txn_headers.action` is
  non-null — buy/sell/dividend/contribution/…) is tied to holdings + lots
  (ADR-0019/0064), which this leg-repoint does **not** carry. Moving one — in
  either direction, including investment→investment and investment→bank — is out
  of scope, so both the single-row and the bulk endpoint reject an
  investment-shape header (`transaction-header-is-investment`). This is enforced
  in the data layer, not just the UI, so the invariant holds regardless of
  caller. A pure-cash row (`action` null — the only shape a bank register holds)
  is what moves — including, legitimately, a **single-posting** cash row moving
  **to** a brokerage (a mis-filed deposit), so investment accounts remain valid
  destinations. Only the split-into-investment case below is rejected.
- Target must be a **real account** (not a category) in the **same ledger**.
- The `UNIQUE(header_id, posting_index, account_id)` invariant (ADR-0022) means
  a posting can't have two legs on one account: if the target account is already
  a leg on the transaction (i.e. moving one side of a **transfer** onto its other
  side), reject it — that would be a self-transfer.
- A **split** (multi-posting) transaction can't move to an **investment**
  account: brokerages hold security/lot postings (ADR-0019), not categorized
  cash splits, so a split there would be malformed. Reject it. (A single-posting
  cash row moving to a brokerage — e.g. a deposit — is fine.)
- Bulk is **all-or-nothing** (ADR-0024): if any selected row would collide (or
  hit the split-into-investment guard), the batch is rejected with a clear count,
  nothing changes.

## UI walkthrough

**Move** is available in **every** view — select rows in any register (checkbox
per row or select-all) and the bulk-action bar shows **Move to account…** (an
account-picker dialog). Re-filing a mis-put transaction is therefore one step
(select → Move), with no need to delete/hide it first. Move is disabled for the
same read-only selections as Delete (a row whose canonical owner is elsewhere —
investment header or split counter-side — can't be moved from this register).

**Unhide** is specific to the **Hidden** tab: selecting it re-fetches the
register with `hidden=true` and the rows render muted; the bulk bar then adds
**Unhide** alongside Move. This is the path for a transaction that was already
deleted (soft-hidden) — unhide it, or move it to the right account, or both.

The selection machinery already handles a single checked row, so there are no
separate per-row menu items — selecting one row and using the bar is the
single-row path. Single-row `unhide` / `move-account` API endpoints still exist
(for MCP / future callers) and are tested.

## Consequences

- The hot register read path gains a visibility-mode parameter (a migration to
  `register_entry_keys()` + the repo). Default behavior is unchanged; covered by
  the register tests.
- `is_hidden`/account are structural, not override columns — unhide and move are
  direct mutations (not `txn_*_overrides`), consistent with how delete and
  recategorize already work.
- This is the supported fix for import-to-wrong-account; no raw SQL, and it
  doesn't touch the (deliberately ledger-wide) FITID dedup.

## Open items / TBD

- Whether the Hidden tab is per-account only or also ledger-wide — start
  per-account (the register is account-scoped); revisit if a ledger-wide "trash"
  is wanted.
