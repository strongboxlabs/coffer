# 0043 — Unified account/category picker + frequent-counterparties

* Status: Accepted
* Date: 2026-06-09
* Related: ADR-0002 (categories are accounts), ADR-0029 (investment
  transaction editor), the `Typeahead` primitive, the OFX/QIF
  importers (which produce transfer-to-category legs)

## Context

The account/category dropdowns in the transaction editors were
built ad-hoc on the `Typeahead` primitive, and the inconsistency
causes friction:

1. **Truncation.** `Typeahead` defaults to 8 visible rows. Only
   `SecurityField` overrode it; the Category, Transfer, and Fee
   pickers silently capped at 8 — a ledger with dozens of
   categories couldn't browse past the first few.
2. **Ambiguity.** Rows showed only the account/category name. Names
   need not be unique (several categories named "Fees" under
   different parents is legitimate), and the picker gave no way
   to tell them apart.
3. **A latent correctness bug.** The shared `useIdBackedTypeahead`
   resolved the typed/picked text back to an id by **string
   equality on the name**. With duplicate names, it silently
   resolved to the first match — picking the wrong account.
4. **No domain separation or grouping.** A mixed picker (Transfer,
   once it began accepting categories) showed assets and every
   category in one flat, undifferentiated, capped list.
5. **No recency.** Every pick started from a cold alphabetical
   list, even though a given account's transactions overwhelmingly
   reuse the same handful of counterparties/categories.

## Decision

Build one shared **`AccountCategoryPicker`** combobox and adopt it
across the account/category fields. It is a bespoke component (the
flat `Typeahead` can't express filters + grouped sections + a
pinned group + the keyboard model), taking the full ledger account
list plus the caller's `isEligible` predicate.

### Behavior

- **Every match shown**, inside a `max-h` scrollable panel — no row
  cap.
- **Filter buttons** `[All · Accounts · Categories]` with keyboard
  shortcuts **Alt+1 / Alt+2 / Alt+3**. The buttons appear only when
  the eligible set spans both domains (so single-domain fields —
  Category, Fee — stay chrome-free).
- **Grouped sections:** a pinned **Frequent** group (see below),
  then **accounts by type** (Bank, Investment, …), then
  **categories by Income / Expense**.
- **Per-row qualifier:** account type, or category kind + immediate
  parent name (`Expense · Business`) — disambiguates duplicate
  names.
- **Id-based selection.** Picking (click or keyboard Enter on the
  highlighted row) commits the item's **id**, never a name string.
  This eliminates the duplicate-name resolution bug class. Free
  typing only filters; it never resolves to an id, so an ambiguous
  blur can't silently pick the wrong row.

### Frequent — derived from history, not a usage table

`GET /api/ledgers/{lid}/accounts/{aid}/frequent-counterparties`
returns the source account's most-used counterparty **accounts**
and **categories** (top 3 of each). It is a pure read over existing
`txn_legs` (a `GROUP BY` mirroring `GetSimilarPayeesAsync`) — **no
usage-tracking table**, so it's always accurate, needs no
write-path, and is cacheable per `(ledger, account)`. System
placeholders (`Uncategorized`, Holdings siblings) and inactive
accounts are excluded — they're noise, not useful picks. The picker
pins these at the top under a "Frequent" header (intersected with
the field's eligibility).

The ranking is a **frecency score** with three refinements so it
predicts what the user will actually pick on the field being edited:

1. **Posting-paired counterparty.** A "counterparty" is the leg
   sharing the source leg's `posting_index` (the symmetric-posting
   pair, same definition `resolved_transactions` uses) — not every
   leg on the header. On a paycheck split the funding bank pairs
   with each category at its own posting; the co-occurring
   tax/insurance/wage legs sit on other postings and don't count as
   the source's counterparties.
2. **Recency weighting.** A use within 90 days counts ×4, within a
   year ×2, older ×1 — the picker tracks how the user banks now, not
   five years ago.
3. **Split dilution.** Each transaction contributes ~1 unit of
   ranking weight spread across the distinct counterparties the
   source touched on it: a singleton gives its one counterparty a
   full 1, an 8-way paycheck split gives each category 1/8. This
   keeps recurring multi-counterparty splits (payroll being the
   canonical case) from crowding out the counterparties a user picks
   on simple one-off transactions — which the split set rarely
   overlaps. The displayed use-count stays the honest raw header
   count; only the *ranking* is dilution-aware.

### Layers (per the layer-independence principle)

- **DB/API:** the aggregation endpoint
  (`AccountsRepository.GetFrequentCounterpartiesAsync` + the route).
- **UI:** the `AccountCategoryPicker` component + the thin field
  wrappers (`CategoryField`, `TransferField`, `FeeField`) that
  supply `isEligible` and forward `frequent`.

## Scope of this slice

Adopted in the **investment editor** fields: Category, Transfer,
Fee. `SecurityField` keeps its own picker (it's over securities,
not accounts, and already shows-all + holdings-prioritize +
create-new).

**Adopted everywhere as of 2026-09-15.** The bank register's `TxnRowEdit` uses
it for the single-posting branch and for every leg of a split. A
system-account counterparty (the bank-feed sync stamps incoming rows with
Uncategorized) round-trips through the picker's own rule: the displayed name
comes from the FULL accounts map, so the counterparty still shows although it
is not offered for a fresh pick.

The splits grid (2026-09-16) uses one additive prop, `invalid`, which marks the
control without the message `error` renders below it — that message would push
a leg row past the grid's fixed height budget. The panel needs no room inside
the scroller, because it is fixed to the viewport (ADR-0103 D7).

## Consequences

### Positive
- One friendly, consistent picker for accounts + categories;
  future fields inherit it.
- Duplicate names are distinguishable and selected correctly
  (closes a silent mis-resolution bug).
- The common case (reused counterparties) is one click away.
- No new schema or write-path for "frequent" — it's a read.

### Negative / trade-offs
- A bespoke combobox is more code than a `Typeahead` wrapper, and
  duplicates some popover/keyboard logic the primitive also has.
  Justified by the grouping/filter/pin requirements the primitive
  can't meet.
- "Frequent" counts counterparty legs across the account's history;
  a one-off heavy query per editor open (cached 60 s). Bounded —
  it's one account's counterparties, not the whole ledger.

## Alternatives considered

- **Just raise `Typeahead`'s default row cap.** Fixes truncation
  only; leaves ambiguity, the resolution bug, grouping, and
  frequents unaddressed. Rejected as a band-aid.
- **A usage-tracking table for frequents.** More infrastructure +
  a write-path on every save, for data already derivable from
  history. Rejected.
- **Tabbed panel (two tabs) instead of filter buttons.** Rejected:
  filter buttons keep one list visible, add less click-friction, and degrade cleanly
  to no-chrome for single-domain fields.

## Update (2026-07-22) — categories as a tree, path-typing, copy/paste

The v1 picker rendered categories as a FLAT alphabetical leaf list with a
`kind · parent` tag. It had three gaps (visible on the bank editor's Category
field), all closed on the same shared component:

1. **No copy/paste.** The closed field showed the full path (`Bills/Electricity`)
   but focusing it wiped the value to an empty query — so it couldn't be selected
   + copied, and a pasted path matched nothing.
2. **Leaf-based, not tree-based.** The flat list gave no visual hierarchy.
3. **No path navigation.** `Bills/` returned "No matches" (`/` wasn't parsed).

Resolution — categories now render as a **root-first tree** (parents as rows,
children indented) split into Income / Expense, every node selectable. Filtering
is **path-aware**: `Bills/El` navigates to Bills › Electricity (query segments
substring-match consecutive path components, anchored at the leaf), a trailing
slash lists a subtree, and a plain term still fuzzy-matches any component (so a
parent name reveals its subtree). **Copy/paste** is a round-trip: opening
pre-fills the selected path and selects it (copyable; first keystroke replaces),
and typing/pasting a full path + Enter commits it directly. Real ACCOUNTS keep the
flat type-grouped list (no tree). Selection stays id-based; the Frequent pin,
domain tabs, and Alt+1/2/3 are unchanged. The tree + path-match logic lives in a
pure, unit-tested helper (`categoryPickerRows.ts`).

## Update (2026-11-04) — opening on a value the picker does not offer

A current value can be one the picker never offers for a fresh pick: any value
the caller's `isEligible` refuses, such as the system Uncategorized category the
bank feed stamps on new rows, or an inactive category or account. It is shown
from the full accounts map (above), and opening on it pre-fills its full path and
selects it as for any value, so the copy round trip still works. That text does
not narrow the list until the user types: it matches no offered row, so the list
would read "No matches". The full list shows instead, with no row highlighted:
the value is in none of the rows, so Enter picks nothing until the user moves to
a row or types. The first keystroke replaces the selected text and narrows the
list as usual. A value the picker offers opens as before, narrowed to its own row
and its ancestors. This holds wherever the picker opens pre-filled: the
register's editors, the home screen's Review, and every other field built on it.
