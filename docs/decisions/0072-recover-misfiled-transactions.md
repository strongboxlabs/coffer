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

> **Amended 2026-10-09 (v0.114.0 and the release after it) — the guards were
> rewritten.** The list below replaces the original, four of whose bullets had
> stopped being true. Two causes. First, the original treated "target is a
> category" and "header is investment-shape" as whole-row refusals, when the
> hazard each guarded was narrower: crossing between a category and an
> account, and moving a leg holdings derive from. Both over-refused — a
> category register could not recategorise, and a dividend's category could
> not be corrected at all. Second, the rule existed **three times** — the bulk
> endpoint, the single-row endpoint and the SPA's picker — and the copies
> drifted: bulk refused banking ↔ investment while the single row allowed it.
> An attempt to reconcile them by hand copied the bulk rule onto the single
> row and silently revoked the "a cash row may move to a brokerage" decision
> below; only the tests encoding it caught that. The rule now has ONE
> implementation, `BulkTransactionsRepository.CheckHeadersAsync`, which both
> endpoints call over a header query (one header, or the whole selection); the
> SPA's picker mirrors only its account-level half.

**Guards** — the same for the single-row and the bulk endpoint, enforced in the
data layer so they hold whatever the caller:

- **A category and an account are not interchangeable**
  (`transaction-move-domain-crossed`). The move is `UPDATE txn_legs SET
  account_id = target WHERE account_id = source`; crossing that boundary
  re-points a category leg onto a money account (or the reverse) and leaves the
  transaction with no category at all. Category → category is allowed — that is
  a recategorise, and it is the operation a category's register should offer.
- **Banking and investment accounts ARE interchangeable**, in both directions,
  for a single cash row: a brokerage deposit filed against checking, or a
  contribution filed against the brokerage that belongs in checking. Banking is
  deliberately wide — bank, credit card, asset, liability, loan and cash — since
  bank → card is a correction people really make.
- **A split cannot cross between banking and investment, either way**
  (`transaction-move-split-to-investment`, `transaction-move-split-from-investment`).
  Only a single banking leg moves; a brokerage holds security and lot postings
  (ADR-0019), not categorised cash splits.
- **An investment event's money leg stays where it is**
  (`transaction-header-is-investment`): its holdings would stay behind
  (ADR-0019/0064). Its **category** leg is free to move — holdings and lots
  derive only from legs carrying a `security_id` (migration 068), and a security
  leg lives on the holdings sibling, so recategorising a dividend strands
  nothing. The cash leg of an investment event that landed in a **bank** stays
  refused too, for a different reason: that register treats the row as read-only
  ("edit + delete belong on the brokerage register"), and a move that bypassed
  it would contradict the surface.
- **The one exception: a cash-only `transfer` may leave a brokerage for a
  bank**, and is converted to a plain bank transfer as it goes. Cash-only means
  no security leg, no second posting, and nothing on the far side but a banking
  account — an MD-imported contribution from checking is exactly this. After the
  move it touches no investment account, so it cannot stay an investment event:
  `action` is cleared on the header and `posting_role` on **every** leg, in the
  same transaction. Both or neither — `posting_role IS NOT NULL ⇔ action IS NOT
  NULL` (migration 057), and since migration 084 dropped its trigger nothing in
  the database checks it, so every writer must keep it. A brokerage → brokerage
  transfer does not qualify: moved to a bank it would still touch one. Nothing
  derived is stranded — holdings, lots and realized gains are all keyed off
  security legs, which a cash-only transfer has none of.
- **The target must be active** (`account-inactive`). Only the picker used to
  enforce it, so MCP could move rows onto an account the user had retired. The
  source is deliberately not checked: moving rows OFF a deactivated account is
  how one is cleaned up.
- The `UNIQUE(header_id, posting_index, account_id)` invariant (ADR-0022) means
  a posting can't have two legs on one account: if the target account is already
  a leg on the transaction (i.e. moving one side of a **transfer** onto its other
  side), reject it — that would be a self-transfer.
- Bulk is **all-or-nothing** (ADR-0024): if any selected row is refused, the
  batch is rejected and nothing changes. The transfers a bulk move will convert
  are resolved **before** the legs move — the selection is scoped to the source
  account, so asked afterwards it would match nothing and the conversion would
  silently not happen.

## UI walkthrough

**Move** is available in **every** view — select rows in any register (checkbox
per row or select-all) and the bulk-action bar shows **Move to account…** (an
account-picker dialog). Re-filing a mis-put transaction is therefore one step
(select → Move), with no need to delete/hide it first. The picker offers only
targets on the register's own side of the category boundary — categories on a
category's register ("Move to category…"), money accounts everywhere else.

Move does **not** share Delete's read-only gate, and originally did. Delete is
right to refuse a row whose canonical owner is elsewhere — removing a split's
counter-side here would destroy a transaction another account owns — but a move
touches only the legs on THIS register's account, so a split's leg can be
recategorised from here. What the bank register still disables is an
investment-owned row on a non-category register, matching the server's guard;
everything finer (a split, a trade, a non-cash transfer) is the server's call,
and its refusal renders inline in the dialog.

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
