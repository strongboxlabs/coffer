# 0072 — Recover mis-filed transactions: hidden view, bulk unhide, move-to-account

Status: Accepted; D3 amended 2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (a move relabels and keeps
every stored figure; a two-amount pair that would end up in one currency with magnitudes
that differ is refused, and a mirrored pair that lands across currencies becomes a
two-amount pair with equal magnitudes and opposite signs; extended 2026-11-16, D15: a
move is refused when moved figures carry more places than the target's currency allows);
D3 amended 2026-11-18: the single-row move route is removed, and a move of one row is a
bulk move of an explicit selection of one (settled by Claude, 2026-11-18); D2 amended
2026-11-18: the single-row unhide route is removed, and a restore of one row is a bulk
unhide of an explicit selection of one, which restores and counts only hidden rows
(settled by Claude, 2026-11-18)
Date: 2026-07-08
Relates: [ADR-0003](0003-immutable-feed-and-overrides.md), [ADR-0022](0022-txn-headers-and-legs.md), [ADR-0024](0024-register-bulk-selection.md), [ADR-0034](0034-header-walk-running-balance.md), [ADR-0068](0068-mcp-write-surface.md)

## Context

An OFX/QIF file imported into the wrong account leaves no supported way back. The
register "delete" of the resulting rows is a **soft-hide** (`txn_headers.is_hidden = true`) for feed rows
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

`POST /api/ledgers/{ledgerId}/transactions/bulk-unhide` (a `SelectionRequest`)
restores one row or many; a restore of one row is an explicit selection of one.
Mirrors the delete path's soft-hide, inverted: set `is_hidden = false` (only on
rows currently hidden), then the ADR-0034 balance + holdings recompute for the
affected accounts (same explicit call-site as bulk-delete). Hard-deleted manual
rows are gone and out of scope — unhide only resurrects soft-hidden rows.

> **Amended 2026-11-18 (settled by Claude, 2026-11-18).** The single-row
> `POST /api/ledgers/{ledgerId}/transactions/{headerId}/unhide` is removed: no screen
> called it, and a path the app never calls is dead code. The bulk unhide restores and
> counts only the selected rows that are hidden, so a visible row in the selection is
> left as it is and not counted, and it ignores an unknown or merged-away id
> (ADR-0114 D15).

### D3 — Move to a different account

`POST /api/ledgers/{ledgerId}/transactions/bulk-move-account`, body carries the
**target account** and the `SelectionRequest` whose `AccountId` is the source scope.
A move of one row is an explicit selection of one.

> **Amended 2026-11-18 (settled by Claude, 2026-11-18).** The single-row
> `POST /api/ledgers/{ledgerId}/transactions/{headerId}/move-account` is removed: no
> screen called it, and a path the app never calls is dead code. Every guard below was
> already the bulk move's, and its tests now run through the bulk move. The removal
> also took away a way to write to a row merged into another, which the single route
> accepted and the bulk move never did (ADR-0114 D15).

Mechanism: repoint the **source-side leg(s)** — the leg(s) on the *current*
account (`account_type != 'category'`) — to the target, a **direct
`txn_legs.account_id` update** (the same kind of mutation recategorize already
does to the category-side leg, ADR-0068), then recompute both the source and
target accounts. For a split, every source-side leg (one per posting) moves, so
the whole transaction relocates. `external_id`/`provider_key` stay on the header
(the ledger-wide FITID dedup is unchanged — the move fixes placement without a
re-import).

> **Amended 2026-10-09 (v0.114.0 and the release after it) — the guards below
> replace the original list.** Two defects in it. First, "target is a category"
> and "header is investment-shape" were whole-row refusals, when the hazard each
> guards is narrower: crossing between a category and an account, and moving a
> leg holdings derive from. As whole-row refusals they over-refuse — a category
> register cannot recategorise, and a dividend's category cannot be corrected at
> all. Second, a rule implemented **three times** — the bulk endpoint, the
> single-row endpoint and the SPA's picker — drifts: bulk refused banking ↔
> investment while the single row allowed it. Reconciling copies by hand risks
> carrying one copy's rule onto another and silently revoking the "a cash row
> may move to a brokerage" decision below, which tests encode. The rule now has ONE
> implementation, `BulkTransactionsRepository.CheckHeadersAsync`, which the move
> calls over the selection's header query; the SPA's picker mirrors only its
> account-level half. (Since 2026-11-18 the bulk endpoint is the only move route.)

**Guards** — enforced in the data layer so they hold whatever the caller:

- **A category and an account are not interchangeable**
  (`transaction-move-domain-crossed`). The move is `UPDATE txn_legs SET
  account_id = target WHERE account_id = source`; crossing that boundary
  re-points a category leg onto a money account (or the reverse) and leaves the
  transaction with no category at all. Category → category is allowed — that is
  a recategorise, and it is the operation a category's register should offer.
  *(Amended 2026-10-27, [ADR-0108](0108-memo-and-tags-per-side.md) D3, D6: the move is not only that `UPDATE`.
  A transaction the source owned, which after the move has no leg on it, hands
  its ownership to the target (`AnnotationWriter.FollowRepointAsync`). No memo
  or tag is rewritten: they sit on legs, and the legs keep their ids, so each
  stays with its line. The stored memo-and-tags presentation of every moved
  header is recomputed in the same transaction.)*
- **Banking and investment accounts ARE interchangeable**, in both directions,
  for a single cash row: a brokerage deposit filed against checking, or a
  contribution filed against the brokerage that belongs in checking. Banking is
  deliberately wide — bank, credit card, asset, liability, loan and cash — since
  bank → card is an ordinary correction.
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
- **A pair that cannot be represented is refused** *(amended 2026-11-14,
  [ADR-0114](0114-benchmarks-and-currency-conversion.md) D13)*. The move relabels: every stored figure is kept. A
  two-amount pair whose two sides would end up in one currency with magnitudes that
  differ cannot be represented, so the move is refused. A mirrored pair that lands across
  currencies becomes a two-amount pair with equal magnitudes and opposite signs, and the
  move's result reports how many did. When it is above 0 the Move dialog shows it and
  stays open on that line until it is closed by Done, Escape or the dialog's close
  (ADR-0114 D13, decided by the owner, 2026-11-18). *(Extended 2026-11-16, ADR-0114
  D15.)* A move into an account in another currency is also refused when the moved
  figures carry more places than that currency allows, and the refusal names how many
  do not fit (settled by Claude, 2026-11-16).
- **The target must be active** (`account-inactive`). Enforced only in the
  picker, it would let MCP move rows onto an account the user retired. The
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

Move does **not** share Delete's read-only gate. Delete is
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
single-row path. The single-row `unhide` and `move-account` API endpoints were
removed 2026-11-18 (see D2 and D3).

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
