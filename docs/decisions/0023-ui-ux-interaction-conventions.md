# 0023 — UI/UX interaction conventions and modern web defaults

* Status: Accepted; amended 2026-11-14 and 2026-11-16 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (§H: the symbol comes
  from the shared formatter, every money input shows it muted, its step and blur
  rounding follow the currency's minor units, and a transfer between currencies shows its
  second amount; every money figure is shown, typed and stored at its currency's ISO 4217
  minor units, read from one data file shared by the API and the SPA, prices are
  formatted apart from money, money columns stay right-aligned, and the currency pickers
  offer every accepted code; §C and §Q: the second amount sits right of Amount, on the
  Balance track in register edit mode, where the faded pre-edit balance is then not
  shown)
* Date: 2026-05-12
* Companion to: [ADR-0021](0021-ui-layout-and-principles.md)

## Context

ADR-0021 fixes the **visual treatment** — tokens, primitives, layout
shell, sidebar conventions, the workflow-dense slate-teal-light
direction. It does not say anything about **how the user interacts
with components**: where buttons sit in a dialog, what keys do in
an edit form, whether double-click is a real gesture, how typeaheads
commit their selection, how to display signed amounts, what happens
when a save fails.

In the absence of an interaction-layer doc, every UI surface
re-litigates those choices — per-cell versus row-level edit,
`[Save] [Cancel]` (legacy desktop order) versus `[Cancel] [Save]`
(modern web order), and the contract between a Typeahead's
`onChange` and a form's commit, which races when it is not pinned.

This ADR pins those conventions so the next register surface,
settings page, or modal inherits them by default. The default is **modern web** rather
than legacy desktop (Win95-era OK/Cancel order, Java/Swing
register conventions) — Coffer's users are a mix of MD refugees
with desktop muscle memory and people who haven't touched MD;
modern-web is the broader convention.

## Decision

### A — Button order

**Affirmative-right, cancel-left.** `[Cancel] [Save]`,
`[Cancel] [Delete]`, `[Cancel] [Continue]`. Primary action
rightmost in its visual group. Web convention (Apple HIG,
Material, GitHub, GitLab); the cursor naturally lands there
coming off the rightmost editable field.

Reject legacy Win95 / OK-Cancel order even though MD uses it.
Discoverable: the affirmative button gets `variant="primary"`
(filled accent), Cancel gets `variant="secondary"` (outline).

### B — Edit triggers for tabular data

**Double-click** on a row enters edit mode for that row. No
pencil icons, no per-cell commit-on-blur. The row's
`title="Double-click to edit"` is the discoverability fallback.
*(2026-11-03, [ADR-0109](0109-overview-is-an-attention-first-home.md): for an owner
or editor. A viewer's register opens no editor on double-click or Enter, and its rows
carry no such title.)*

This matches Moneydance / Quicken muscle memory and avoids the
per-cell trap where each click commits independently. Single-
click on a row is reserved for **focus** (see §B.1 below) — a
distinct state from selection, which is checkbox-gated.

### B.2 — A single-field numeric cell may commit in place (2026-09-18)

§B forbids per-cell commit-on-blur, and ADR-0099 D1 requires exactly that for
the budget screen's Target cell. One of the two had to move on the record rather
than be quietly contradicted, and it is this one — narrowly.

**The exception.** A cell may commit in place when the row has EXACTLY ONE
editable field, the field is a single scalar, and the surface is not a
transaction register.

**Why it does not reopen what §B closed.** §B's stated objection is the
"per-cell trap where each click commits independently" — a hazard that needs two
or more editable cells to exist at all. You escape one, land in another, and a
half-finished row is committed a piece at a time with no coherent point of
review. A row with a single editable field has no second cell to escape into:
the commit boundary and the row boundary are the same thing.

**Why not row-edit mode instead.** That is the faithful reading of §B, and it
would put a Cancel/Save footer on every row to set one number — on a screen
whose entire premise (ADR-0099) is that there is nothing to configure and the
numbers are useful before anyone types one. The ceremony would cost more than
the protection is worth here.

**What the exception still requires.** Escape abandons the draft — without it
the only exit from a half-typed number is to blur, which commits it. An empty
field is a distinct instruction, not a zero. And a cell whose value cannot be
accepted renders DISABLED with the reason on hover, rather than taking
keystrokes it will reject.

Registers keep double-click-to-edit unchanged. They are multi-field by nature,
which is precisely the case §B is about.

### B.1 — Selection and focus are independent states

Two row states co-exist in the register and never collapse into one:

- **Focus** is the keyboard cursor — at most one row. Drives
  ArrowUp / ArrowDown navigation and is the target of an Enter that
  opens edit (per §D below).
- **Selection** is the multi-row checkbox state — zero or many rows.
  Drives bulk actions (delete, tag, reconcile).

The conflation trap (single-click toggles the checkbox) breaks the
Enter-to-edit flow because the row that just became "selected" isn't
where the user's keyboard cursor is. Keeping the two separate matches
Gmail's row model (the checkbox toggles selection; the row body sets
focus; Cmd/Ctrl-click extends selection).

| Input | Effect |
|---|---|
| Plain click on row body | Set focus to this row, clear previous focus. Selection unchanged. |
| Cmd/Ctrl-click on row body | Toggle selection of this row. Focus unchanged. |
| Click the row checkbox | Toggle selection of this row. Focus unchanged. |
| Double-click on row body | Open inline edit (per §B). |

Visual treatment (paired with ADR-0021 tokens):

- **Focused** — `bg-accent-soft/15` + 2-pixel inset shadow in
  `--color-accent-muted` on the leading edge.
- **Selected** — `bg-accent-soft/40` + 2-pixel inset shadow in
  `--color-accent` on the leading edge.

The states compose — a row can be both focused and selected; the
"selected" treatment wins on the leading-edge shadow because it's
the stronger signal.

### B.1.5 — Status badge as a click target for reconciliation

The status badge in the register's status column is **the click
target for cycling the reconciliation state** of the row. The cycle
order matches the visual progression:

```
uncleared → reconciling → cleared → uncleared
```

Click is the single gesture — there is no separate dropdown or
context-menu item duplicating this. The badge's `title` attribute
spells out the cycle so it's discoverable on hover.

Disabled targets:
- **Scheduled rows** (future-dated) — you can't reconcile something
  that hasn't posted; the badge renders read-only.
- **Pending rows** — the feed source dictates this state; the user
  can't override it from the register.
- **A viewer's register** (2026-11-03, [ADR-0109](0109-overview-is-an-attention-first-home.md))
  — a viewer cannot write, so no badge cycles.

Implementation: the badge is wrapped in a `<button>` for non-disabled
states. The click handler stops event propagation so it doesn't
trigger the row's own click handler (focus / select). Status writes
go through `PUT /api/.../recon-status` which manages the paired
`cleared_at` / `cleared_by_user_id` audit columns server-side.

### B.2 — Register-level keyboard navigation

When focus is on the register (no active edit form, no typing
target), keyboard moves the focused row:

| Key | Action |
|---|---|
| ArrowDown | Move focus to next row, scroll into view if needed. |
| ArrowUp | Move focus to previous row, scroll into view if needed. |
| Enter | Open inline edit on the focused row (per §B and §D). |
| Space | (Reserved) Toggle selection of the focused row — pending implementation. |

Bail on Enter / Arrow keys when the active element is a text-like
input (`<input>` of type text/number/email/etc., `<textarea>`,
`contenteditable`) — those shortcuts belong to the field, not the
register.

**Two further bail-outs on Enter** (added 2026-09-16), each preventing a
double-fire:

- **`event.defaultPrevented` is set.** Something nearer the event already
  claimed this Enter — an open ContextMenu activates its highlighted item with
  `preventDefault()` and deliberately does not stop propagation, so without the
  check the same keypress also opens the editor on the row behind the menu.
- **The focused element is a `BUTTON`.** A register row carries two — the
  status-cycle control and the split-parent's expand toggle — and Enter is
  their NATIVE activation key, so without the check one keypress cycles the
  status AND opens the editor. `defaultPrevented` cannot catch this: native activation
  is the event's DEFAULT ACTION and runs after every handler, so nothing has
  marked the event by the time the row handler sees it.

Checkboxes stay exempt, unlike buttons: a checkbox does nothing useful with Enter,
so letting the row handler fire is what makes Enter-after-checkbox-click open
the editor reliably.

**A key pressed in a popup is the popup's** (added 2026-11-04). None of these
shortcuts, N to create included, acts on a key pressed inside an element with
`role="dialog"`, `role="menu"` or `role="listbox"`: a `Modal` over the
register, the date-jump popover (§B.4), a row's menu (§B.3), the header's Show
and Sort options. The rows are behind the popup, so N would open a new row under
it, and an arrow, which a menu uses to move between its items, would also move
the row focus out of sight. Inside is read from the key's path, as §L reads a
press, so a key on a control in the popup that takes itself away as the key goes
down is still the popup's.

Enter on a **split-parent** row opens the multi-leg editor, the keyboard twin
of the double-click (since 2026-09-16).

Implementation note: focus is tracked as `focusedRowId` in the
register page component, distinct from `selectedIds`. The list
library (react-virtuoso) is driven via `scrollIntoView({ index })`
to keep the focused row visible without snapping the viewport;
virtuoso no-ops the scroll when the index is already on-screen, so
arrow-key navigation between visible rows doesn't cause re-anchoring.
After expanding a split group (a height change), the scroll-into-view
call is deferred 100ms so virtuoso's ResizeObserver has measured the
new row heights — otherwise it would compute the scroll target
against the pre-expansion height and stop short of the bottom-most
expanded leg.

### B.3 — Per-row context menu (right-click)

**Right-click on a register row opens an actions menu** anchored at
the cursor. The menu is the canonical home for row-scoped operations
that aren't first-class enough to merit a dedicated toolbar button.

The bank register's menu today: **Accept** (needs-review rows), **Edit**,
**Duplicate**, **Create reminder**, **Show other side**, **Delete**. Future
actions (attach receipt, …) land here too. *(2026-11-03,
[ADR-0109](0109-overview-is-an-attention-first-home.md): a viewer's menu, in both
registers, holds Show other side and Show raw data only, `rowActionsFor`.)*

**Edit** (2026-09-16) exists for split parents specifically: without it a
split parent has no menu route to its own legs, and a double-click is the only
pointer way in. Its `shortcutHint` reads `Enter`, which is honest because §B.2's
Enter opens the editor on a focused split parent; note that hints in this menu
are decorative — the component renders them and binds nothing, and `⌘D` on
Duplicate is not bound anywhere.

Menu items:

| Item | Behaviour | Disabled when |
|---|---|---|
| Duplicate | Opens the new-transaction form prefilled with the source row's payee / memo / amount / counterparty. `posted_at` defaults to today (the user usually wants the duplicate dated today). *(2026-10-27, [ADR-0108](0108-memo-and-tags-per-side.md) D1: the memo and tags copied are the ones this register shows, an inherited value included.)* | *(2026-10-27, ADR-0108 D1)* Not offered on a read-only row: a split line seen from its other side, or an investment event's counterpart row. |
| Show other side | Navigates to the counterparty account's register with `?focus=<headerId>` so the receiving page scrolls + focuses the matching leg. | Counterparty account isn't visible to the user (RLS-filtered) or the row has no counterparty leg. |
| Delete | Confirms via modal, then sends `DELETE /transactions/{id}`. Server hard-deletes manual entries (`external_id IS NULL`) and soft-hides feed / import-keyed rows (`is_hidden=true`) so a re-source doesn't resurrect them. | — |

Keyboard inside the menu follows ADR-0023 §L (modal/popover
dismissal) plus the per-menu specifics:

| Key | Action |
|---|---|
| ArrowDown / ArrowUp | Move highlight (wraps; skips disabled items). |
| Enter / Space | Activate the highlighted item; menu closes. |
| Esc | Close and return focus to where it was when the menu opened, as §L returns it to a dialog's trigger. The Esc is marked handled and stopped, as a list's is (§F's marking convention), so the parent's cancel handler does not also fire; an Esc something nearer already marked is not the menu's. Focus left in the closed menu would fall to the page, so in the split editor's row menu the next Esc would not reach the editor to cancel it. |
| Tab | Close + advance focus naturally. |

Implementation: a custom `ContextMenu` primitive in
`src/Web/src/components/ui/`. We considered Radix's DropdownMenu but
hand-rolled to stay consistent with the existing Typeahead (zero new
deps in this PR; switch to Radix when the menu count grows past
~3 surfaces). Reusable across the app — file ADRs against this
section if a future menu surface needs different behaviour.

### B.4 — Bulk-action footer

When ≥1 row is selected (checkboxes), a sticky footer at the bottom
of the register surfaces bulk actions. The footer is always rendered
but the action buttons only appear when something is selected — the
"N selected · Σ $X.XX" readout is the discoverability cue.

Available bulk actions for this PR:

| Button | Effect |
|---|---|
| Mark cleared | Set `status='cleared'` on every selected header. |
| Delete | Confirm via modal; then call DELETE on each selected header. Server-side per-row policy still applies (hard-vs-soft based on `external_id`). |
| Clear | Empty the selection set. |

**Esc clears the selection**, as Clear does, with two exceptions. It does nothing
while focus is in a field (an input, a textarea, a select, editable text), where
Esc belongs to the field. And it does nothing when something nearer already used that Esc and
marked it with `preventDefault`: the inline editor cancelling on an Esc from any
of its controls, its Save and Cancel buttons included (§E), or a list closing on
one (§F), which stops it as well. One Esc never both cancels the edit and clears
the selection. The register's date-jump popover (Ctrl/⌘+J) is a list by that
convention: its Esc closes it and nothing else, whether pressed inside it or with
focus elsewhere on the page. Closed by Esc, Cancel, Jump or the shortcut, it gives
focus back to where it was when it opened, as §L and §B.3 require, unless focus
has moved on since; when nothing had focus then, or that control has left the
page by now, focus goes to the register's focus home instead (§L), never to the
page. A press outside leaves focus where the press put it (§L). So opened from an
inline editor, the next Esc cancels the edit; opened from the page, the next Esc
is the selection's.

The date jump is a popover like the header's Filter, Show and Sort (§L), not a
modal (since 2026-11-04). Nothing behind it is blocked, and it does not claim to
block the page: no `aria-modal`, no backdrop. Tab may leave it, and it stays open.
A press outside closes it and goes on to what it landed on, which keeps the focus
that press gave it. It stays a non-modal dialog named "Jump to date", so a key
pressed in it is its own and not the register's (§B.2). Its own shortcut follows
§B.2 too: Ctrl/⌘+J pressed inside a dialog, a menu or a list is that popup's and
does not open the date jump (pressed inside the open date jump, it closes it). It
sits on the header popovers' layer, below every dialog, so a dialog opened while
it is open covers it as it covers the rest of the page.

The footer also reserves slots for Categorize / Tag (disabled today —
their endpoints don't exist yet). Bulk reconcile is mark-cleared
only; we don't surface "mark reconciling" / "mark uncleared" because
the per-row badge click handles those cases more naturally.

Selected leg ids resolve to owning headers before any bulk operation —
selecting two legs of the same multi-split entry collapses to one
header in the action set. The sum sums each header's amount once
(`groupAmount` for groups; the txn's `amount` for singles).

The footer's trailing slot is intentionally minimal post-migration to
bidirectional sliding-window pagination: it shows a low-key "Loading…"
indicator only while an edge-load is in flight. No "Load more" button,
no "End of register" sentinel — both were artefacts of the
append-only model. The register free-scrolls; the loader is
invisible.

The window is capped (`useWindowedRegister` enforces `MAX_ENTRIES = 1000`
+ `EVICTION_HYSTERESIS = 100`), so a long scroll session evicts whole
pages from the far edge as new ones load. Scroll position is anchored
via virtuoso's `firstItemIndex` so the user never sees the rendered
rows shift under them. If they reverse direction past the eviction
boundary, the affected edge briefly shows the "Loading…" indicator
while the evicted page re-fetches — same UX as a fresh edge load.

**Bulk-selection scale.** Selection state lives in the
`useSelection` hook (ADR-0024) — a discriminated explicit-vs-`'all'`
union, with the `'all'` shape carrying a `selectedAt` anchor + an
exclude set. The header-checkbox click flips between
`explicit-empty` and `'all' mode`; "select all" means *every
matching row in the current view*, not just the loaded window.
Footer count + Σ come from a server `selection-summary` endpoint
(debounced ~200ms; since ADR-0024's 2026-10-28 amendment, an explicit
selection's read-only part is counted from the loaded rows only while it is in
flight) so the readout stays accurate across window
eviction and across the `'all'` predicate. Bulk-recon-status +
bulk-delete fire one request each — the server resolves the
predicate and applies the change in one atomic Postgres
transaction.

**Typed confirmation for large deletes.** When the bulk-delete
target count exceeds 100, the confirm dialog requires the user to
type `delete <N>` exactly before the Confirm button enables. The
threshold is set in `RegisterPage.tsx` and the typed-confirm
mechanism lives on the `ConfirmDialog` primitive
(`requireTypedConfirmation` prop) so future destructive bulk
flows (export-purge, merge-revert, …) reuse it without reinventing.

### B.5 — Show-Other-Side arrival (focus seed)

The `?focus=<headerId>` arrival path (right-click → "Show other
side" → counterparty register) anchors a fresh register on the
target row. virtuoso's `initialTopMostItemIndex` can't carry this
on its own — it's read once at mount, when the sliding-window
fetch hasn't yet resolved the focus index. We surface the row
explicitly via:

1. The API's `?starting_at=<headerId>` returns a page with the
   focused entry at index 0 + older entries after it (migration
   031, ADR-0019).
2. After the fetch lands, RegisterPage mirrors the focused row
   into local focus + selection state and calls
   `virtuosoRef.current?.scrollIntoView({ index, align: 'start' })`
   on a 100ms deferred tick so virtuoso's ResizeObserver has
   measured the rendered rows before the scroll request.

The 100ms defer is the same pattern §B.2 uses for split expansion.
Both cases share the same root cause: scroll math against
yet-to-be-measured row heights.

### C — Edit layout: in-place row expansion

When a row enters edit mode, the row's height grows in place and
the static cells are replaced with inputs in the same column
positions. The container is two lanes:

- **Lane 1** — mirrors the static column grid. Each editable
  cell becomes an input; non-editable status badges and reference
  values (e.g., pre-edit balance) stay visible at reduced
  opacity. *(Amended 2026-11-14, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D14: on a transfer between
  currencies the second amount, Received or Sent, takes the Balance track, so the faded
  pre-edit balance is not shown while it does.)*
- **Lane 2** — full-width below lane 1. Holds inputs that don't
  fit the column grid (e.g., memo) and the `[Cancel] [Save]`
  buttons right-aligned at the end.

Errors render in a `role="alert"` banner below lane 2 with
`text-state-danger` + `bg-state-danger-soft`.

Avoid: modal dialogs for row edits, side-panel slide-outs (those
break the user's place in the register), per-cell editing (the
trap C is designed to replace).

**Postings-list extension (ADR-0025).** Single-row and multi-split
transactions share one editor (`TxnRowEdit`) whose body is a
**vertical stack of posting rows**, one per leg on the source
account. The column-grid alignment in §C applies only to the
header line (date / payee / header memo) at the top of the
form; below it the postings list breaks out into its own
full-width layout because an arbitrary-N posting editor doesn't
fit the static row's column grid. Per-posting affordances:

- `⋮` left-edge drag handle — reorder via HTML5 native drag.
  The new order maps directly to server-side `posting_index`
  (ADR-0025 reconcile).
- amount + counterparty Typeahead + leg-memo input — three
  columns sized to the editor's own breakpoints, not the
  register's.
- `[−]` remove — disabled when only one posting remains.

Adding a new posting uses a **ghost row** at the bottom of the
list (Notion / Airtable / Linear pattern): a faded placeholder
posting row that materialises into a real one on focus or Tab.
No "+ Add posting" button; no menu choice for "Convert to
split." The single → split conversion is just adding postings;
split → single is removing all but one.
*(2026-10-27, ADR-0108 D4: the memo and tags move with the shape. Splitting
copies the memo and tags this side showed onto the transaction and its first
line; removing the second-to-last line joins the transaction's memo with the
line's ("transaction · line", when they differ) and merges their tags. The editor shows the
joined memo and merged tags in its fields before Save, with no separate
notice.)*

`Total: $X.XX` readout below the postings list is
informational only — there's no sum-constraint warning (the
schema's invariant is per-posting, not transaction-wide).

> **Superseded in part — 2026-09-16 (PR \#547).** Everything above about
> per-posting affordances is now wrong, and the ADR-0025 amendment of the same
> date carries the reasoning. Restated rather than rewritten so the original
> choices stay legible:
>
> * The legs left the register's grid for **their own five columns** —
>   `# · Category+Tags · Memo · Amount · actions` — which is what dropped a leg
>   row from ~62px to 30px and made a fixed-height scroll viewport practical.
>   The "three columns sized to the editor's own breakpoints" is now five.
> * The **`⋮` drag handle moved from the left edge to the right-hand actions
>   cell**, and drag is no longer the only way to reorder: Move up / Move down
>   live in a per-row menu with Alt+↑ / Alt+↓, because there is no drag on
>   touch and none at all from a keyboard.
> * **`[−]` remove became "Remove split"** in that same row menu — a 3.5rem
>   actions cell cannot hold three controls, and the menu is the visible path
>   Rule 10 of [ADR-0021](0021-ui-layout-and-principles.md) prescribes.
> * **The ghost row is gone**, and "No '+ Add posting' button" is reversed by
>   an **Add split** button in the region's sticky footer. This is the reversal
>   worth understanding, because the ghost-row pattern is otherwise sound: a
>   ghost is the LAST ELEMENT of the list, so once the list scrolls inside a
>   fixed-height viewport the only way to add a posting scrolls away with it.
>   On a many-leg paycheck you would scroll to the bottom to reach it every time.
>   The pattern assumes a list that grows the page; it does not survive a
>   viewport.
> * The **`Total:` readout moved INTO that sticky footer**, so it stays visible
>   while the list scrolls. It is still informational, and that part is
>   load-bearing: [ADR-0025](0025-transaction-as-postings-list.md) rejects any
>   sum constraint, and two test files now pin the absence.
>
> The counterparty control is also no longer a Typeahead — it is
> `AccountCategoryPicker` ([ADR-0043](0043-account-category-picker.md)). The two
> components once made opposite Esc choices; since 2026-11-04 they follow one
> rule, see §F.

*(Amended 2026-11-14, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D14: the splits grid gains one column right of
Amount, with a header caption, for a line whose counterparty is a real account in
another currency, so its five columns are six while it shows. The reasoning is in
[ADR-0025](0025-transaction-as-postings-list.md)'s amendment of the same date.)*

### D — Keyboard inside edit forms

| Key | Action |
|---|---|
| Tab / Shift-Tab | Native browser focus order. Skips disabled inputs automatically. |
| Enter | **Saves the row.** Exception: inside a Typeahead with a highlighted suggestion, the first Enter picks the suggestion (`preventDefault`) and the second Enter (popover now closed) saves. |
| Esc | **Cancels the row.** Exception: with a list open (the payee or security suggestions, the category picker, the tags list), the first Esc closes only the list and the edit stays open; the next Esc cancels the row. The same holds in a dialog, where the second Esc closes the dialog. What the field shows once its list is closed differs by list; see §F. |
| Ctrl/⌘+Enter | Always saves from anywhere — escape hatch when Enter is consumed by a child (textarea, future rich-text fields). |

All keyboard shortcuts surface via `title` tooltips on the
relevant buttons ("Save (Enter or ⌘+Enter)", "Cancel (Esc)") so
users see them on hover without needing this doc.

**When the row closes, focus goes to the register's row list** (added
2026-11-04), never to the page, whether Esc cancelled it or Enter saved it:
the register's focus home (§L). The same holds for the new-row form.

### E — Cancel semantics

**Cancel discards local edit state with no confirmation prompt.**
Modern web default. Triggers:

1. The Cancel button.
2. Esc (at form level, with no list open: an Esc that closes a list goes no further, §F).
3. Click anywhere outside the edit container (sidebar, toolbar,
   table whitespace).
4. Switching to another row's edit (implicit cancel via the
   single-slot `editingId` state).

**Focus after the edit closes** (added 2026-11-04). Closing takes the editor's
controls away, and focus left on the page would sit outside every control. So
however an inline edit or the new-row form closes, by Esc, by Cancel, or by a
Save, Accept or Post that closes it, focus goes to the register's row list, its
focus home (§L). A click outside (3) leaves focus where the click put it, and
focus the user moved elsewhere while a save ran stays there. Switching to
another row's edit (4) opens that row's editor, which takes focus itself.

The "are you sure you want to discard your changes?" dialog is
warranted only when the user is editing destructive content
(deleting an account, dropping a ledger, mass-revoking sessions).
For an in-place row edit the cost of accidental discard is one
re-type — the cost of a nag prompt is interrupting every edit.

### F — Typeahead selection contract

`<Typeahead>` is a controlled input with a filtered suggestion
popover.

- **Selection is a pure `onChange(label)`** — exactly the same as
  the user typing the label. No separate `onCommit` / `onCancel`
  callback pair. The parent owns the value; the popover is just
  a faster way to put text in.
- **Filter**: case-insensitive substring against
  `getSearchableText` (defaults to `getLabel`). For compound
  paths (account paths joined by `/`), pass the full path as
  `getSearchableText` so a query like "Food/Groc" matches the
  parent chain and the leaf.
- **Keyboard**: ↑/↓ moves highlight; Enter picks highlight +
  `preventDefault` (so parent form's Enter handler skips); Tab
  picks + lets focus advance (no preventDefault); Esc with the
  list showing closes it, keeps focus and the typed text, and
  goes no further (`preventDefault` + `stopPropagation`); Esc with
  no list showing (nothing matches, or it was just closed) passes
  to the parent form, whose Esc cancels the row.
- **Click outside** closes the popover without firing `onChange`
  (preserves user-typed value for the parent's Save to read).
- **Click on item** picks via `onChange` (same path as Enter on
  highlight).

The contract eliminates the React-setState-batching race where a
parent's commit handler would read a stale value because
`onChange(newValue)` and `onCommit()` ran in the same callback.

**One Esc rule for every list in the editors** (amended 2026-11-04). Every
list in the bank and investment editors (the payee suggestions, the security
search, `AccountCategoryPicker` ([ADR-0043](0043-account-category-picker.md)),
and the tags list) treats Esc the same way: with the list open, the first Esc
closes only the list, the edit stays open, and the event is marked with
`preventDefault()` and stopped with `stopPropagation()`; with no list open, Esc
cancels the edit. In a register the
row stays open on the first Esc and closes on the second; in a dialog (Post or
skip, the series editor, Review…) the dialog stays open on the first and closes
on the second, by §L's rule for an Esc a control already used.

What the field shows once the first Esc has closed its list is each list's own:

- **Payee and security**: focus stays in the field, and so does the typed text.
- **Category picker**: focus stays in the field, which shows its saved category
  again. The text typed to search is dropped, as it is whenever the list closes
  without a pick. The Esc is the list's wherever in the control focus is: from
  the list's filter buttons (All · Accounts · Categories), which Tab reaches
  before the next field, it closes the list the same way and focus returns to
  the field without reopening it.
- **Tags list**: the filter goes away with the list and its text is cleared.
  Focus returns to the button that opened the list; left on the page, the next
  Esc would never reach the editor.

The payee and security lists close on blur. The category picker and the tags
list close when focus lands on an element outside the control, so a Tab or
Shift+Tab out of either closes it, while a move inside the control (the picker's
filter buttons, the tags list's checkboxes) does not. Moving back from the
picker's filter buttons to its field keeps the text typed to search and the
filter chosen; only focus arriving with the list closed opens it afresh from
the saved category. A press on either list
leaves focus where it is unless it lands on one of the list's own fields (the
tags list's filter and checkboxes). A press on a part that cannot take focus (a
tag's name, a heading in the category list) would otherwise send focus to the
nearest ancestor that can, which in a dialog is the dialog's panel, outside the
control. Left on screen with focus outside the control, a list could not catch
the Esc meant for it, and that Esc would cancel the edit or close the dialog.

A split's row menu (Move up, Move down, Remove split) works the same way, by
§B.3: its Esc closes the menu and gives focus back to where it was when the menu
opened, the row's actions button when that opened it, so the next Esc cancels
the edit.

**The marking convention** (2026-11-04). A list or popover that handles an Esc
(closes itself on it) marks it with `preventDefault()` and stops it with
`stopPropagation()`, both in the handler that closes it. Anything further out
that acts on Esc (a dialog's close, an editor's cancel, the register selection's
clear, a menu) ignores an Esc whose `defaultPrevented` is true. Each half covers
what the other cannot: a stop does not reach a listener that runs first or that
React's propagation never passes through, and a mark is no use to a handler that
never reads it. A web test, `EscapeRule.test.ts`, holds every Esc handler in the
SPA to its role from a catalog that gives each file a role (list, consumer, both,
or a field ending its own edit, each with a reason) and the number of Esc checks
it holds. It fails on a file the catalog does not list, on a check added to a
listed file, and on an Esc handled in a shape it cannot read; a consumer's look at
the mark counts only where it stops the action, and a list's mark and stop count
only as calls on the same event whose key it compared (the native event's stop does
not stop React's propagation).

The editors read `event.defaultPrevented` rather than assume what a child did.
That check is not pedantry. An editor that does not make it closes the list
*and* cancels the whole edit on one Esc — dismiss a dropdown you opened by
mistake, lose every leg of a many-leg split, with no undo and no draft.

This replaces an earlier pair of contracts (recorded 2026-09-16) in which the
Typeahead let its first Esc bubble, so an Esc that dismissed the payee or
security suggestions also cancelled the edit or closed the dialog, while the
category picker already kept the edit open.

### G — Disabled-but-visible placeholders

Features that are planned but not yet implemented should appear
as **disabled controls with explanatory tooltips**, not hidden
UI. Example from the register edit form: the Category input is
disabled with
`title="Editing category is the next slice — under ADR-0019
changing the counterparty is a delete-and-recreate of the leg
pair, not an override."`

This:
- Sets the right expectation ("this is coming") without making
  the user wonder if they missed it.
- Provides a built-in reminder of the constraint / next slice.
- Keeps the layout stable when the feature actually lands.

Avoid: silently omitting controls that "should be here"; ghosted
icons with no tooltip explaining why; mysterious `(coming soon)`
hover text without a real reason.

### H — Signed money display

**One signed Amount column, not separate Outflow / Inflow.**

- Format: `Intl.NumberFormat` with `signDisplay: 'auto'` →
  `-$X.XX` for negatives, `$X.XX` for positives (no leading
  plus).
- Colour: `text-state-danger` for negatives, default `text-text`
  for positives. Green-for-positive (`text-state-success`) reads
  as "look at all this income!" — misleading on a register that's
  mostly spending.
- Right-aligned, decimal-aligned, `font-mono tabular-nums`.
- Scheduled rows: drop the danger colour (everything muted —
  the row's own muting carries the signal).

*(Amended 2026-11-14, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D14: `$` above is only an example. The symbol
comes from the shared formatter in `lib/money`, whose locale follows the browser and
which puts the symbol before or after the figure where `Intl` puts it, so CA$ is told
apart from $. The other leg's amount a register row shows on a transfer between
currencies uses this format: the symbol, and no plus.)*

*(Amended 2026-11-16, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D15.)* `X.XX` above is a two-place currency's
shape. Every money figure is shown, typed and stored at its own currency's ISO 4217
minor units, 0, 2 or 3 places, not at two places for every currency: a 0-place currency
shows whole units and a 3-place currency shows three decimals. The places come from one
checked-in data file of ISO 4217 exponents, compiled into both the API and the SPA, with
a test that flags drift between them. `lib/money` passes the digits to `Intl`
explicitly and never relies on `Intl`'s own defaults, since the browser's CLDR data
disagrees with ISO for several currencies. Prices, share quantities and exchange rates
are not money and do not follow this rule:

- **Money** (an amount, a balance, cost basis, market value, an unrealized gain, a trade
  amount) is formatted by `formatCurrency` at its currency's minor units, everywhere.
- **A price** (current price, cost per share) is formatted by `formatPrice`, everywhere.
  A price shows at least its currency's places and at most max(6, places) (settled by
  Claude, 2026-11-16). `HoldingsPanel` and `SecurityDetailPage` converge on this split.
- **A converted or combined figure** the API returns (REST and MCP) keeps its 4-place
  internal bound, and the SPA's formatter presents it (settled by Claude, 2026-11-16).

Money columns stay right-aligned in `font-mono tabular-nums`, with no slot reserved for a
missing fraction. "Decimal-aligned" above therefore holds within one currency only:
where currencies with different places mix in one column, their decimal points do not
line up, and that is accepted.

**Reach (amended 2026-10-31).** The rule is the same on every
surface, not only the register:

- **Red marks a negative that is news:** a transaction amount, in
  both registers and their editors; a change (a net-worth change, an
  unrealized loss); a net below zero.
- **Default text for everything else:** every positive, gains
  included, and every total whose sign is fixed by what it is
  (expenses, liabilities, balances). Such a total would be red
  permanently, so red would stop meaning anything.
- **Nothing is green.** `state-success` is a status colour (a
  succeeded pill, a done notice, a check mark), never an amount's.
  `greenIsStatus.test.ts` lists every file allowed to use it, with
  the reason. A gain or a percentage carries no leading plus.
- **An unposted amount is muted**, never red or green: a reminder
  amount wherever it appears, and the lines of a Scheduled split as
  well as its parent.
- **The other leg's amount is muted** *(amended 2026-11-14,
  [ADR-0114](0114-benchmarks-and-currency-conversion.md) D14)*: on a transfer between currencies, the register row's
  counterparty cell shows the other leg's amount after the account
  chip, muted and never red. In the bank editor the second amount
  (Received or Sent) is a transaction amount like Amount, red when
  negative.

For data-entry inputs, accept a signed number in one field
(`type="number"` with a one-cent `step`). Don't split outflow/inflow
into two inputs.

*(Amended 2026-11-14 and 2026-11-16, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D14 and D15.)* Every money input
shows its currency's symbol inside it, muted and not typed, from the same formatter; the
input still takes a signed figure. The one-cent `step` above holds for a two-place
currency only: every money input's step and its rounding on blur follow its currency's
minor units, read from the shared ISO 4217 data file (ADR-0114 D15), so a currency with
no minor unit takes whole units. That covers Amount and the second amount alike.
Received or Sent, beside Amount on a transfer between currencies, is not an
outflow/inflow split: it is the other account's own figure, with no sign segment, and
its sign follows from Amount. Like every money input, it shows its own account's symbol.

The SPA rounds money with one helper, `roundToMinorUnits`, half away from zero, which
replaces every `toFixed(2)` and ×100 money site and is applied to the save body too.
While a currency is null (accounts still loading) nothing is rounded. A price input takes
`step="any"` and gets no money rounding. These three rules are settled by Claude,
2026-11-16. A typed amount finer than its currency's places that reaches the API is
refused with a 422 and a named error code that names the currency and its places
(settled by Claude, 2026-11-16).

The account and ledger currency pickers offer every accepted ISO 4217 code, in the same
native select: the 14 codes they offered before ADR-0114 D15 first, then the rest
alphabetically. CLF and UYW, the fund units with four places, are not offered, nor is
any code ISO lists with no exponent, nor any code ISO does not list; none of these is
accepted as an account or ledger currency. A code already stored that would now be
refused for any of these reasons keeps working at two places, and the consistency check
lists it as an advisory, not counted in its verdict (settled by Claude, 2026-11-16).

Reject: parentheses for negatives (accounting tradition; throws
off decimal alignment of tabular-nums); always-coloured-positive
green; separate outflow/inflow columns from MD/Quicken legacy.

### I — Errors

- **Field-level validation errors**: inline near the field with
  `role="alert"` and `aria-invalid="true"` on the input. Helper
  text below the input describes what's valid.
- **Mutation errors (server returned 4xx/5xx)**: banner at the
  bottom of the form with `role="alert"`,
  `border-state-danger/40`, `bg-state-danger-soft`,
  `text-state-danger`. Use the API's `detail` text from
  `ApiError`.
- **Network / unknown errors**: same banner shape with a generic
  fallback message. Don't show the stack trace.

### J — Loading states

- **In-flight mutations**: button label transitions
  ("Save" → "Saving…", through `pendingLabel`, which lays both
  labels out from the first render so the button never changes
  size) and the button is pending
  (§L): it keeps focus and refuses a second press, and is never
  `disabled` for it. Avoid replacing the entire button with a spinner — the label
  going to "Saving…" reads instantly; a bare spinner adds a "what
  is this" pause.
- **Query loading**: skeleton rows preferred over generic
  "Loading…" text where the layout has a natural shape to mimic.
  Plain `Loading…` is fine for small panels.

### K — Forms — labels and layout

- **Labels above inputs**, not beside. Better for responsive +
  screen-readers.
- **Inputs are full-width** of their grid cell. Don't fix-width
  text inputs to "look nice" — alignment is what makes them look
  nice, not arbitrary widths.
- **Helper text** below inputs (`text-[0.6875rem] text-text-muted`),
  inside the input's labelled-by chain via `aria-describedby`.
- **Required fields**: don't mark them special unless most fields
  are optional; mark *optional* fields with "(optional)" in the
  label instead.

### L — Modal / popover dismissal

Esc dismisses. Backdrop / outside-click dismisses (unless the
modal is a destructive confirm). Modal content stays focused
until dismissed — focus trap inside the modal, return focus to
the trigger on close.

**The trigger is what had focus before the dialog opened** (added 2026-11-04).
`Modal` records it while React puts the dialog on the page, before anything
inside can take focus: a field with `autoFocus`, or an effect of the dialog's
content. A dialog whose field focuses itself therefore still gives focus back to
the control it was opened from, and a dialog opened over another (Add security,
from the security field of the editor behind it) gives it back to that field. A
dialog that opens as another closes, in one step (a delete confirm's Merge
instead…), gives focus back to the first dialog's trigger. When the first
dialog's action is a write that the next dialog shows the result of (the
recovery-codes confirm, handing over to the fresh codes), the first dialog's
trigger is pending while the write is, not disabled (below), so it takes focus
back as the first dialog closes, and the next dialog gives focus back to it.
When the trigger cannot take focus back as the dialog closes (it is disabled by
then, or gone), focus goes to a fallback the host names (`Modal`'s
`fallbackFocusRef`): closing "Copy your token now" gives it to the token name
field, since the made token clears the name and leaves Generate disabled until
a new one is typed. A snapshot that brings the ledger to its cap disables
"+ Create snapshot", so closing Create snapshot gives focus to the new
snapshot's row, its first control; the dialog closes only once the list has been
refetched, so the render that closes it draws that row. The Watchlist settings'
picker works the same way: the add that brings the chosen categories to their
cap disables the picker while it holds focus, and focus goes to the new row's
Remove.

**A dialog opens where its content puts focus** (added 2026-11-04). When the
dialog's content has already taken focus as it mounts (a field with `autoFocus`,
or content that focused itself), `Modal` leaves focus there. Otherwise it focuses
the host's `initialFocusRef`, else the first focusable control, else the panel.
So Add security opens on Name, the ticker filled in from the security field; Add
price opens on Price, the date filled in; and a dialog whose transaction editor
mounts with it (a reminder occurrence's Post or the series editor, when what they
read is already loaded) opens on the editor's Date, not on the header's Close. Edit
security opens on Name as Add security does, since both render the same fields.
The trigger is recorded as before, ahead of the content, so closing still gives
focus back to the control the dialog was opened from. The same holds for a dialog
that opens as another closes in one update: `Modal` reads the content's focus as
soon as the content has taken it, before the closing dialog gives focus back to
its own trigger, and opens on that field.

**A button whose request is pending keeps focus** (added 2026-11-04). While a
request the button sent, or waits on, is in flight, the button is busy, not
unavailable: it keeps focus and its place in the tab order, says so with
`aria-disabled` and `aria-busy`, and refuses to act. Every way of activating it
arrives as a click (a press, Enter or Space on it, Enter in a field of its
form), and that click is refused, so a second press sends no second request. It
looks as a disabled button does. The native `disabled` attribute is for a
button that is unavailable for another reason (a required field still empty,
nothing changed yet, no permission): a focused button that becomes disabled
drops focus to the page, and after a refusal from the server the dialog or form
stays open with nothing in it focused. One mechanism holds this, in
`components/ui/pending.ts`: `pending` on `Button`, `IconButton`,
`RowActionsButton` and an `ActionFooter` slot, `isConfirming` on
`ConfirmDialog`, and `usePendingButtonProps` on a raw button. A field whose own
Enter runs the action (a dialog's name field, the bank feed's setup token, a
row editor's memo, a typed confirmation) presses the button rather than calling
its handler, so the button refuses it while unavailable, pending or held (below),
and the field stays enabled, so the field Enter was pressed in keeps focus.
When the request's own success leaves the button it was
pressed on disabled (it cleared the field the button waits on, or left nothing
changed), focus goes to that field, never to the page (`fallbackFocusRef` on
`Button`): a field disabled while the request was pending takes it once it is
enabled again, and focus the user has moved since stays where it is. Where the
button waits on several fields, focus goes to the first: a backup retention save
that lands leaves nothing changed, and focus goes from Save to Daily. A web
test, `PendingButtonRule.test.ts`, fails a button's `disabled`, or a
hand-written `aria-disabled` or `aria-busy`, that reads a request's pending
state, and lists every other with why the button is unavailable, so a new one
is reviewed before it ships. What that test does not see is stated in it.

**A field whose request is pending keeps focus too** (added 2026-11-04). The
button rule applies to fields: an input, select, textarea, checkbox, radio,
switch or field component whose request is in flight keeps focus and its place
in the tab order, refuses input, says so with `aria-disabled` and `aria-busy`,
and looks as the field primitives' disabled field does (a not-allowed cursor at
half opacity): a field with disabled styles of its own maps those, and a raw
field with none takes the primitives' look for its disabled state as well as
its pending one, so the two never differ. Native `disabled` stays for a field
that is unavailable for another reason (no permission, a choice that does not
apply): Enter in a sign-in username submits the form, and a username disabled
by the submit would drop focus to the page. A text-like field is `readOnly`
while pending, so typing, pasting and a date or number field's own picker and
steppers change nothing. A choice has no `readOnly`, so its change is refused:
a change is not handed on (React puts a clicked checkbox or radio back to its
controlled value), any other choice's click and a press of the pointer are
cancelled (a select's list and a file field's picker stay shut), and a key that
would change it or open its list (Space, a letter, an arrow, Enter) is
cancelled, while Tab still leaves it. A field component (the account and
category picker, the payee and security search, the date field) takes `pending`
and opens no list, commits no pick and runs no shortcut while it is set. One
mechanism holds this, in `components/ui/pending.ts`: `pending` on `Input`,
`Checkbox` and the field components, `pendingTextProps` on a raw text-like
field, and `pendingChoiceProps` on a raw choice, which takes the field's
handlers. The same test fails a field's `disabled`, or a hand-written
`readOnly`, `aria-disabled` or `aria-busy` on a field, that reads a request's
pending state, and a field with its own change, click, key or press handler
beside the `pendingChoiceProps` it spreads; it lists every other field
`disabled` with why the field is unavailable. It cannot see a field that feeds a
request and carries no marker at all, since nothing in the source says which
request a field feeds, so each surface's own tests hold that every field of a
form whose request is in flight refuses input, not only the ones its success
clears: a bank feed's setup token, a notification target's add form on both
surfaces (its provider, job, name, URL, severity and upgrade-notices box), the
Upgrade tab's phrase and private-fork token, the master key's rotate phrase, a
report's Save as… name and Share box, the category dialogs' name, kind, Top
level, parent and merge-target fields, the backup passphrase dialog's two fields,
and a connection's rename field.

**An edit surface refuses changes while its save is in flight** (added
2026-11-04). When a save's success clears the draft it saved, a change made to
the draft while the save is in flight would vanish with it, unsaved and
unannounced. So every control that changes the draft is pending with the save,
as the save's own Cancel and Reset are. On the Overview, while Done saves: the
Columns choice (`SegmentedControl` takes `pending`, and refuses a press, Enter
and Space on every option), each frame's move, other-column, column-choice,
settings and Hide controls, the tray's Add, and an open settings panel's Apply;
a drag does not start and Alt+↑/↓ moves nothing. Each control keeps focus.

**From the press until the action has settled** (added 2026-11-04). The pending
state a page passes in renders some time after the press (TanStack Query tells
a page its mutation is pending on a later task), so the press itself holds the
button: every request the press starts (a mutation or fetch started while its
handler runs, a submit button's form submitted inside the press, a promise the
handler returns) holds the button until each has settled, and a further press,
Enter, Space or implicit submission on a held button is refused as on a pending
one. The hold is taken and checked synchronously, with no timer. A press that
starts no request (a refusal of what was typed, a press that opens a confirm)
holds nothing, so the next press acts. A handler starts its request in the
press, not after an await or a timer; another way into the same action clicks
the button. A press holds its group: controls that wait on the same requests or
must exclude each other (the occurrence dialog's Skip, Post and its editor's
save; the MCP panel's two switches; a saved report's Save and Save as…'s Save;
the quote providers' switches; the upgrade settings) are wrapped in one
`PendingScope`, and a press of any of them holds them all until what it started
has settled, so a second, conflicting request never goes out before the pending
state renders. Outside a scope a press holds its own control only, since a list
whose rows each wait on their own request is not one group. A choice whose
change writes (a switch, a role select) is held from the change the same way,
through `usePendingChoiceProps` (Checkbox goes through it when given
`pending`): a second change of it, or of another choice or button in its
group, is refused until the change's request has settled.
`PendingButtonRule.test.ts` fails a pending button's handler that is called or
handed on past the button, that defers its request, or a raw button's own
`onClick` beside its pending props; a button that starts a request and is not
in the mechanism at all (no `pending`, no `isConfirming`, a slot with no
`pending`); and a choice that writes on change without the hold from the change
(the plain `pendingChoiceProps`, a Checkbox with no `pending`, a raw choice's
own `onChange`). What it does not see is stated in it.

**A pending button never changes size** (added 2026-11-04). A label that
changes while pending ("Delete" → "Working…") goes through `pendingLabel`
(`Button`, an `ActionFooter` slot, and so `ConfirmDialog`'s confirm) or
`PendingLabel` (a raw button), which lays both labels out from the first render
in one grid cell: the button's box is the wider of the two, at rest and while
pending alike, so neither the button nor its neighbours move when the label
swaps. The label not shown is invisible and hidden from assistive technology,
so the accessible name is the label shown. A narrower button, right-aligned or
centred, moves its edge from under the pointer, and the second press of a
double click lands beside it, taking focus to the page; a wider one pushes a
right-aligned button and its neighbours aside while a retry is in flight.
Neither label reads a pending state itself, or the box would change with it. A
button pending with its group whose label swaps only for its own request (a
saved report's Save beside Save as…'s Save, Post while a Skip is in flight, one
Repair while another runs) takes a `PendingLabel` given that request's pending
state, and so does an editor's Save through its `submitting` prop; a label that
reads in stages (a passkey ceremony: starting, then waiting on the
authenticator) nests a `PendingLabel` as its pending label, whose own state is
the later stage. The same test fails a button whose children, slot `label` or
`pendingLabel`, or `label` or `…Label` prop read a pending state, the pending
label and `PendingLabel`'s own labels among them.

**An error stays shown until the new result replaces or clears it** (added
2026-11-04). It is never cleared at the press: a refusal above a button in a
centred dialog that went at the press of a retry would re-centre the dialog and
move the button from under the pointer. A submit or save handler does not clear
its error as its request starts; a refusal of what was typed replaces it, the
request's own result replaces or clears it, and an explicit `reset()` when the
page starts over (a new file picked, an edit made) clears it. TanStack Query
clears a mutation's `error` as the next mutate starts, so the SPA takes
`useMutation` from `lib/mutation.ts`, which keeps the last run's error while a
retry is in flight. The same test fails a handler that starts a request and
clears an error, and a file that takes `useMutation` from TanStack directly: an
import of it under any name, a re-export of it or of the whole module, or a
namespace, import-equals or dynamic import of the module.

**A popover closes on Esc too** (added 2026-11-04): the register header's Filter,
Show and Sort, a report's range, and the date jump (§B.4). Its Esc is marked and
stopped by §F's convention, whether pressed inside it or with focus elsewhere on
the page, so it never also clears the register's selection, and an Esc a list
inside it used closes only that list. Closed by Esc or by a pick, it gives focus
back to where it opened from (the button that opened it; for the date jump, where
focus was), unless focus has moved on since; a click outside leaves focus where
the click put it. None of them is modal: Tab may leave one and it stays open,
nothing covers the page behind it, and none claims to block it (`aria-modal` is
a `Modal`'s alone). A press on a control inside it counts as inside, even when the
control removes itself as the press goes down: a mouse pick in the Filter's
category picker takes the picker's list away, and the pick applies with the
popover still open, as it does for a pick made with Enter. One shared helper holds
all of this, so a new popover gets it by using it. The lists, the row menu, the
bank editor, the dialog and the register's shortcuts ask the same helper whether
an event happened inside them, and the investment editor asks it whether a key
came from a dialog it opened (Add security), whose keys are that dialog's. A web test, `OutsidePressRule.test.ts`, lists
every use in the SPA of the DOM members that relate a node to the elements
around it (`contains`, `closest`, `compareDocumentPosition`, `parentNode`,
`parentElement`), each file's with why it does not ask whether an event happened
inside, or why the event's target is still in the page when it asks, and fails
on any use it does not list, so a new one is reviewed before it ships. What that
test does not see is stated in it.

**A popover sits in the Tab order right after what opened it** (added
2026-11-05). Tab from its last control goes on to the control that follows its
opener on the page, and Shift+Tab from its first control goes back to the
opener; it never drops focus to the page. The header's Filter, Show and Sort and
a report's range get this by where they are drawn: each right after its button.
The date jump has no button: its opener is the control that had focus when
Ctrl/⌘+J was pressed, and it sits in the Tab order right after that control,
Tab from the opener going into it and Shift+Tab from the control after the
opener coming back to its last control. The opener is never a field (an input,
a textarea, a select, editable text), where the shortcut does nothing, so the
search box is never one, and never a control in a dialog, a menu or a list,
whose key the shortcut leaves to it (§B.2). Opened from the register's row list,
which is focusable but no Tab stop of its own, Shift+Tab from its first control
goes back to the list itself, not to a row's control inside it. Opened with
nothing focused, or once its opener has left the page, it sits at the start of
the page: Tab from a fresh start, from the browser's own controls, or past the
page's last control, goes into it, Shift+Tab from the page's first control comes
back to its last control, Tab from its last control goes on to the page's first
control, and Shift+Tab from its first to the page's last. Its opener leaves the
page when the register swaps the list area for a placeholder (on a filter, a
sort or a refresh, and while loading or empty), which takes the row list, its
rows' controls, an inline edit's buttons and the bulk bar's buttons with it;
when the list unmounts a row's control as it scrolls out of the window; and when
the opener takes itself away (a filter chip removed, the bulk bar's buttons
when the selection clears, an editor's buttons when the edit closes). The
toolbar's and the header's controls ("+ New transaction", Filter, Show, Sort)
stay.

Closed by Esc, Cancel, Jump or the shortcut while focus is in it or on the page,
it gives focus back to its opener when the opener is on the page. With no opener
(it was opened with nothing focused) or one that has left the page, it gives
focus to the register's focus home (below): the row list, else
"+ New transaction", else the search box, which then stands in for the list as
focus the register puts there does. Focus on another control stays there. A
press outside closes it and leaves focus where the press put it: on the control
it landed on, or on the page when it landed on none. It gives no focus to the
register's focus home, with or without an opener, so it records no stand-in: the
popover closes in the press's mousedown listener, before the press moves focus,
and a stand-in recorded in that moment would let the list take back the focus
the press gave (amended 2026-11-05). The shared helper that gives a popover's
focus back takes such a fallback from its host, for a popover with no opener on
the page, and the date jump's fallback gives nothing for a close a press caused.

The date jump is drawn where the page renders it, not beside its opener. The
opener can be a row's control, which the list unmounts as it scrolls away, or the
list itself, whose wrapper the register replaces on a filter, a sort or a
refresh and swaps for a placeholder while loading or empty, and a popover drawn
inside either would close with it and lose what was typed and the focus. What
places it in the Tab order is focus guards: an element of its own right after the
opener, and one at each end of the popover. Each is an empty element no wider
than a pixel, hidden from assistive technology, that hands focus on as it
arrives, in the direction of the Tab that brought it: the Tab the page heard,
kept only while it is moving focus. A Tab pressed in the browser's own controls
(the address bar) is not heard by the page, and enters it at its first Tab stop
going forward and at its last going back, so a guard reached with no Tab heard
hands focus back when nothing follows it and forward otherwise (amended
2026-11-05); the last Tab the page heard says nothing about it. With no guard beside an
opener, a guard at the start of the page leads into the date jump the same way:
from the open when nothing had focus or what had it is already gone, and from
the moment the guard beside the opener goes. The page is watched while the date
jump is open, and when its opener or that guard leaves the page (the opener's
container removed, or the opener alone from a parent that stays), the guard left
behind is taken away too, so the guard beside the opener leaves the page if its
opener does, and the date jump stays open; an opener moved within the page takes
its guard along. Reaching the popover from the controls around the place the
page renders it passes over it, so it is in the Tab order once, right after its
opener or at the start of the page. Where nothing follows that place, Tab from
the control before it goes on to where the order starts again: the page's first
control, or, with no guard beside an opener, the date jump itself. Where nothing
comes before that place, Shift+Tab from the control after it goes on to the
page's last control, or, with no guard beside an opener, to the date jump's last
control. The registers draw the date jump after every control, so Shift+Tab from
the browser's own controls reaches the guard at its end with no Tab heard. When
no Tab stop outside the date jump follows the guard beside its opener (opened
from the page's last control, such as the bulk bar's last button, or from the
row list with nothing focusable after it), the date jump is the page's last
place in the Tab order, and that guard gives focus to its last control, Jump,
rather than passing over it to the opener (amended 2026-11-05). Otherwise it
passes over the date jump to the page's last control, as it does for a
Shift+Tab the page heard from a control after the place the page renders it.

**A control that replaces or removes itself hands focus on** (added 2026-11-04)
to the control that takes its place, and never leaves it on the page. There it
would sit outside every control, and with a popover still open a register
shortcut would act on the rows under it. The range's Custom dates… gives way to
the two date fields, and focus moves on to the first date, where the next Esc is
still the popover's. In the register's Filter, clearing the tag moves focus to
the tag field, which then shows its suggestions (the first Esc closes them, the
next the popover), and picking a tag moves it to the tag's clear button. In the
filter chips row, removing a chip moves focus to the next chip, else to Clear
all; Clear all, or removing the last chip, takes the row away, and focus goes to
the search box. In Structure's Tags list, Remove N unused goes once its cleanup
has left nothing unused, and focus goes to New tag beside it. In a bank feed's
account row, a mapping that lands swaps the row's controls: Map gives way to
Sync from… and Unmap, Unmap to the account picker and Map, and focus goes to the
first control of the set that takes the pressed one's place. On the Overview's
Bills rows a skip and its undo keep the row and swap Post or skip… and Undo, and
focus goes to the one that appears (for an occurrence that will auto-post, which
has no Post or skip…, to the row's actions button). On the Overview, Customise
and the edit bar take each other's place: Customise goes as the edit starts, and
focus goes to the edit bar's first control; every way out of the edit (Done,
with or without a save, Escape, Cancel and Discard, Reset to default, the stale
layout's answers) takes the edit bar away, and focus goes to Customise. A
report's Save as… form goes when its save lands or it is cancelled, and focus goes
from whichever of its controls held it (the name field, where Enter submits, the
Share checkbox, its Save or Cancel) to the Save as… button. A connection's rename
form, on Account → Security and System → MCP, goes on a save that lands, on Cancel
and on Escape in its field, and focus goes to that row's Rename, drawn in its place;
while the rename is in flight the form is pending (its field read-only, Cancel and
Escape refused), so a refusal still has its form to show in. On the Google Drive
card a landed disconnect swaps the connected controls for Connect Google Drive, and
focus goes from Disconnect, which its confirm gave it back to, to Connect. On the
Upgrade tab, a removed private-fork token takes Remove away once the status is read
again, and focus goes to the New token field beside it. On the Backups tab, Hide
takes away the revealed passphrase it sits in, and focus goes to Show, else to
Change. The budget's Target cell swaps both ways: its button gives way to the
number field, which takes focus, and the field gives way to the button on Enter or
Escape, which takes it back; a blur has already put focus somewhere else, and it
stays there. On Settings → General's Maintenance panel, a repair that fails
partway, from which no answer comes back, or whose check after it does not finish
takes the report away with every control in it, and focus goes from whichever of
them held it (the Repair pressed, another Repair, pending and still a Tab stop, a
link to what disagrees) to Check consistency, the panel's next step. A repair that
heals its figures keeps the row and takes its Repair away, with the row's list of
what disagrees, and focus goes from that Repair or a link in that list to the next
Repair drawn, else the previous one, else Check consistency, as a removed row's
control hands focus on (below) (amended 2026-11-05). One
shared helper holds the swaps within a row or toolbar,
`useFocusHandOff` in `components/ui/rowRemovalFocus.ts`: the control that goes
takes its ref, and names the control that takes its focus. A part of the page that
goes with controls of its own (the consistency report) takes the ref of its
focus-within form, `useFocusWithinHandOff`, and hands focus on from whichever of
them held it.

A row deleted or removed from its own menu or button takes that control with it
(added 2026-11-04), whether its confirm gave focus back to the control before the
list was read again or the row went while the confirm was still open. Focus goes
to the same control on the next row, else on the previous row, else to the list's
add control, never to the page: New tag, New category, New account, + Create
snapshot, + Create backup, Create invite link; where the add control is disabled
until a field is filled, that field (a passkey's nickname, a token's name, a bank
feed's setup token, a notification target's URL). A list with no add control
gives focus to its own region instead: the element that holds the rows and stays
on the page when none is left, focusable but no Tab stop (`tabIndex` -1), named,
and ringed for keyboard focus only (`focus-visible`), as the app's other
focusable containers are. That is the precedent of the Overview's Bills rows,
which fall back to their widget's panel, and of a register, which falls back to
its row list (below). It holds on the admin's MCP clients and connections (each
list's panel, named by its heading), import layouts (the Import layouts panel),
saved reports (a region named Saved reports, holding the count line and the
cards, which stays when the last card goes), the reminders calendar when its
last chip button goes (the month's calendar), and your sessions (the list
itself, which keeps this device's row). A control that empties its list and goes
with it does the same: the MCP write log's Clear log, and the clients' Prune
unused when it leaves no client, whose confirm names the list as its
`fallbackFocusRef`. The next and previous rows are
those drawn when the row went (a category tree kind by kind, a register's
accounts group by group), and a row whose control is not on the page (a folded
group) or is disabled is passed over. It holds on Structure's three lists
(a delete, a merge, and a deactivate while inactive rows are hidden), snapshots,
backups, members, invites on both surfaces, passkeys, MCP tokens and connected
apps, the admin's MCP clients and connections, bank feed connections, saved
reports, import layouts, notification targets on both surfaces, the
Overview's Bills rows, your sessions (a session signed out leaves the list;
this device's row has no Sign out and is passed over), a security's prices
(each row's edit button, which a click on the row also goes through; + Add
price), Edit security's look-through sleeves (each row's Remove; Add
sleeve), and the Overview's widgets (hidden from their menu, the actions button,
with Customise as home; hidden from the edit frame's Hide, the next frame's Hide,
with the hidden tray's first Add as home; the main column is drawn before the
side), the Watchlist settings' chosen categories (each row's Remove; the
list's add control is a picker that opens its list on focus, so the settings
panel is home, as it is where a closing panel's focus goes), and a split's legs
in the bank editor, and so in the dialogs that host it (Remove split from a
leg's menu: the next leg's kebab, the previous one's, or Add split; removing
one of two legs collapses the split and takes the whole grid away, so focus
goes to the one-posting layout's "Split this transaction →"), and the sidebar's
account rail (an account deactivated while inactive accounts are hidden: the
next drawn row's link or folder button, the previous one's, or the first
section's new-folder button; the rows are read section by section, a folder's
only while it is open, and the selected row's kebab, which no other row has,
hands its focus to the next row's link as the row's link would), and the
reminders calendar's chips (a chip is keyed by its occurrence, so a skip or a
restore keeps the same button and its focus; a post makes the chip read-only,
and focus goes to the next chip button drawn across the month, the chips folded
behind "+N more" left out, else the previous one, else the calendar), and the
Maintenance panel's Repair buttons (above: a row's list of what disagrees goes
with its Repair as the row's companion, and focus on a link in it is handed on as
the Repair's would be; Check consistency is home). A menu drawn
inside the row it removes goes with the row: it asks for the successor before
giving focus back to an opener that is about to leave. A row that has a control of its own is keyed by its own identity,
never by its place in the list, so a removal never hands one row's elements to
the next. One shared helper holds it, `useRowRemovalFocus` in
`components/ui/rowRemovalFocus.ts`: a list gives it its rows' keys and its add
control, or with none its own region, which the helper requires so no list
leaves that focus to the page by leaving it out, and each row's control the ref
it makes. A control that held focus as it
went hands it on in the same render; a dialog or menu opened from a control that
has gone hands it on as it closes (`Modal`, `ContextMenu`). Focus the user has
moved elsewhere stays.

In a register, such focus goes to the row list, the register's focus home
(since 2026-11-04). Rows never take focus, since the arrows move a highlighted
row from anywhere on the page (§B.2), so the list itself is where focus returns:
focusable but no Tab stop (`tabIndex` -1), named "Transactions", and ringed for
keyboard focus only (`focus-visible`), as the app's other focusable containers
are. A press on a row focuses the list, and a browser rings focus a pointer
gave once the next key is pressed (an arrow, Escape), so focus a press gave the
list is marked (`data-pressed`) and shows no ring until the list loses it. The bulk bar's Clear selection takes itself away with the selection, and so
do the bar's buttons when a bulk action clears the selection (a delete, say); an
inline edit and the new-row form take their controls away however they close
(§D, §E). Focus goes to the row list in each case. An existing row's inline
edit is drawn inside the list as a row, and the list draws its rows in a render
of its own after the one that closes the edit, so the edit's field or button
still holds focus as the edit closes: focus is followed until that control has
left the page, and the list takes it then, whether Escape, Cancel or a save
closed the edit. The list is not a typing
target, so the arrows, Enter and N work there as anywhere, and the next Tab goes
on from the list. Only focus that was lost moves: focus the user put elsewhere
stays, and a press outside an editor leaves focus where the press put it. While
the list is not on the page (an empty register draws a placeholder instead: the
last rows deleted, moved or filtered away by a bulk action, the new-row form
closed in an empty account; a loading register draws neither, and a list taken
away while it held focus counts too), focus goes to "+ New transaction", the
register's add control, when it is on the page and enabled, else to the search
box (a category register, a viewer, an account whose currency is not known
yet). That focus stands in for the list: a list that comes onto the page (the
first transaction of an empty account just saved, a refresh landing) takes it
back while it is still where it was put and the reader has pressed nothing
since. So does focus the date jump gives "+ New transaction" or the search box
as it closes with no opener on the page, by Esc, Cancel, Jump or the shortcut
(§B.4; amended 2026-11-05): a filter that takes the list away while the date
jump is open hands focus back to the list when the filtered rows land. A press
outside the date jump gives them no focus and records no stand-in, so focus the
press gave the search box stays there when the list lands.

**Esc belongs to the dialog it was pressed in** (added 2026-11-03). `Modal`
handles Esc on its own panel and stops it there, so a dialog opened from inside
another closes alone, and the editor or page behind it never sees the key. A
control that already used the Esc marks it with `preventDefault`; the dialog then
stays open, and the next Esc closes it. By §F's marking convention a list or
popover closing on the Esc also stops it, so it never reaches the dialog, while
an editor that cancels into the dialog's close marks it only, and the dialog reads
the mark. An Esc pressed while focus is in no dialog closes the newest one only. A host
re-render never moves focus. A dialog taller than the viewport scrolls with its
overlay: the overlay is the scroll container, an inner wrapper at least its
height keeps a short panel centred, and the panel itself never clips, so a list
positioned inside it (a picker's options, the security search) overlays the
panel's edge as it does on a page.

**A scroll dismisses a `position: fixed` menu** (added 2026-09-16). A menu that
captures its coordinates once, at open, cannot follow the row that spawned it:
scroll, and it floats over an unrelated row while still offering the original
row's actions — *Remove split* on the wrong split. Closing is the only honest
response, because a menu cannot follow an anchor it never measured.

Register the listener **capture-phase on `document`**, not on `window`. Scroll
events do not bubble, and this app's document never scrolls — the shell is
`h-dvh overflow-hidden` and each scroll area in it is the containing block of
what it scrolls (ADR-0021 Rule 1), so every scrollport that can move a row is an inner
element (the register's scroll surface, the splits grid's leg viewport, a
dialog body). A bubble-phase window listener fires for none of them, which
looks like working code and is not.

### M — Date input keyboard shortcuts

Every date input in Coffer accepts the following power-user
shortcuts (Quicken / Moneydance lineage; HTML5 doesn't define any
keyboard shortcuts for `<input type="date">`, so there's nothing
to clash with on the web side):

| Key | Action |
|---|---|
| `t` / `T` | Set to today |
| `y` / `Y` | Set to yesterday |
| `+` or `=` | Current date + 1 day |
| `-` or `_` | Current date - 1 day |

When the current value is empty/unparseable, the base for `+`/`-`
is today. The shortcuts are documented in each input's `title`
attribute (`title="Date — keys: t today, y yesterday, +/- shift by day"`)
so they surface on hover without users needing this doc.

Considered MD's wider set (`m` for next month, `h` for previous
month) and rejected — those don't read as obvious to anyone
without prior MD muscle memory and the day-shifts cover the
common cases.

### N — Multi-line text fields and Enter semantics

Free-form text fields with a meaningful chance of multi-line
content (memo, notes, descriptions) use `<textarea>` with
auto-grow up to a capped height, then internal scroll past the
cap. Single-line fields (payee, names, simple labels) use
`<input type="text">`.

Decision rationale: a memo is free text, and an imported memo can
run past one line of register width or to a whole paragraph; a
single-line input would hide everything past the field's edge. Memos
are a notes field, not a one-line tag.

**One deliberate override — the splits grid's per-leg memo** (2026-09-16). It
is a `<textarea>` with a FIXED height and no auto-grow: `rows={1}`,
`h-control-28px`, `resize-none overflow-y-auto`. The leg list scrolls inside a
fixed-height viewport, and a row that can grow makes the viewport's height
budget unpredictable — the thing that lets the editor stay one size at any
split count.

It stays a `<textarea>` rather than becoming an `<input>`, and that part is not
cosmetic: `<input>` runs the HTML value-sanitisation algorithm, which STRIPS
newlines. An existing multi-line leg memo rendered in one would be silently
mangled on first paint and the next save would persist the mangling. Fixed
height, yes; wrong element, no.

**Keyboard inside a textarea (Slack convention):**

| Key | Action |
|---|---|
| `Enter` (no modifier) | Saves the row / submits the form |
| `Shift+Enter` | Inserts a newline |
| `Tab` | Moves focus (focus-trap rules from §D apply) |

This matches Slack / Discord / Linear's short-form-message
convention rather than Gmail / GitHub-PR's long-form-composition
convention (where Enter = newline, ⌘+Enter = submit). Memos in
Coffer are register-scoped short notes — favouring save on Enter
matches the rest of the form's keyboard behaviour. The Slack
convention is also the modern web default for short messaging-
style fields.

**Auto-grow implementation:** hand-rolled in `lib/fitTextareaHeight.ts`, run on
every change, on mount, and again whenever the field's width changes, since a
new width rewraps the text: reset the height to `auto`, read `scrollHeight`
while the field cannot scroll, then set the height to `min(scrollHeight + the
top and bottom borders, max)` (amended 2026-11-04). `scrollHeight` leaves out
the border and the field is border-box, so a height without the border is short
by it and the field scrolls; read while the field can scroll, an overflowing
field wraps its text beside a scrollbar that is gone once it fits, and comes
out a line too tall.
CSS `field-sizing: content` is the future-standard but Safari
adoption is too recent (Safari 18, 2024) to skip the JS path.

### O — Process: surface before code

The conventions above are the **defaults**. When a UI choice has
more than one defensible answer:

1. Lay out 2–3 real alternatives with tradeoffs.
2. Name the modern-web convention explicitly so the user can
   override.
3. Pick a default with reasons.
4. Ask before any JSX.

In short: give two options with tradeoffs, pick the default,
name it. UI/UX gets double the rigour, not the same rigour — the cost of getting it wrong is a
visible regression in the user's daily flow.

### P — Field label casing + editor action bar (added 2026-06, ADR-0049 work)

- **Field labels are small all-caps:**
  `text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted`.
  This is the standard across the app (both register editors and their
  fields, the account/category picker, tables, the reminders calendar,
  securities, snapshots). Settings-form labels built on the `Label` primitive
  (`text-sm`) are a distinct, heavier context — exempt.
- **The editor action bar** (`[Cancel] [Save]`, per A) lives in a **bottom
  footer**, right-aligned, built with the shared `Button` primitive, in both
  register editors. A host (e.g. the reminders occurrence dialog) may inject a
  left-aligned secondary action into the footer's leading slot.

### Q — A value shown from the other side (added 2026-10-27, ADR-0108 D15)

A memo or tag set a side does not own, shown because the other side has it, is
**inherited**, and every surface marks it the same way:

- **The marker.** A small corner arrow (`InheritedMarker`, lucide
  `CornerUpLeft`) before the value, in the subtle text colour; the value itself
  renders as an own value would. The marker names the account the value comes
  from as its tooltip and accessible name ("From *account*"). Chosen over
  brackets, which copy into text as if they were part of the memo, and over
  italic, which is already how a scheduled row reads.
- **The editor caption** (`NotesFieldCaption`). While the side inherits, the
  Memo or Tags caption says where the value comes from ("from *account*") and the
  field shows that value as its placeholder, both as the save will leave them:
  the caption's account is the one in the draft, not the one stored (amended
  2026-11-04, ADR-0108); typing, or picking a tag, gives
  this side its own value and leaves the other side's alone. With an own value
  the caption is the label alone; emptying the field is the way back, and there
  is no second control for it (amended 2026-11-04, ADR-0108). Blank always means
  inherit: there is no explicit "none".
- **The split editor's** transaction-level fields are captioned
  "Memo (whole transaction)" and "Tags (whole transaction)", so they read apart
  from each line's own.
- **One header layout for both bank editors.** The one-split and split editors
  lay their header out alike, on the register's columns: the fields (Date,
  Check #, Payee, the category, Amount), then any hints under Payee (Similar,
  Merge candidates) in a row of their own, then Tax date, Memo and Tags in one
  row, Memo in Payee's column and Tags in the category's. A split leaves the
  first row's category slot empty, since each line carries its own, and keeps
  the column so the total stays over Amount. The hints' own row is what keeps
  Memo and Tags level whatever shows under Payee; a caption too wide for its
  column wraps, and the captions are bottom-aligned so the controls stay level.
  *(Amended 2026-11-14, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D14: while the counterparty is a real account in
  another currency, the one-split header row gains the second amount, Received or
  Sent, right of Amount on the Balance track, with the same caption style and input as
  Amount. While it shows, the faded pre-edit balance on that track is not shown. In a
  split, the second amount lives per line in the splits grid, not in the header.)*
- **An editor's tag control** shows own tag names in the text colour, as the
  memo field shows its text; inherited names keep the marker and the subtle
  colour. Empty, it reads "Tags (optional)", as a line's memo reads "Memo
  (optional)". The register's filter bar has its own tag field, worded for
  filtering ("Any tag").

Read-only rows only display: their memo and tags are edited from the side
that owns the row.

## Consequences

**Positive**
- One reference for "how do I do X in Coffer's UI?" — onboarding
  any new surface starts here, not from re-derivation.
- Future inconsistencies become explicit: any UI that diverges
  needs to update this ADR (and explain why) instead of silently
  drifting.
- The rules live in one place rather than scattered across
  notes.

**Negative**
- Adds a document to maintain. When a new UI pattern ships, this
  doc gets a section appended — easy to forget.
- Some conventions (single Amount column, double-click to edit)
  diverge from MD; users coming from MD will notice. The
  divergence is intentional but adds a moment of "oh, this
  is different" friction.

## Alternatives considered

- **Inline conventions per ADR / per component.** Rejected:
  it doesn't survive multiple surfaces being designed
  independently.
- **Treat this as a follow-ups entry, not an ADR.**
  Rejected: this is a durable architectural commitment about
  Coffer's identity (modern-web over desktop-legacy), not a
  punch-list item.
- **Build a Storybook + design-system website.** Rejected
  *for now*. Useful long-term but premature; the slim ADR is
  enough until Coffer has more than ~5 distinct edit surfaces.

## When this doc changes

Add a section here every time:

- A new UI pattern lands that future surfaces should follow.
- An existing convention is overridden (with the reason in
  context — these are decisions, not preferences).
- A modern-web convention shifts enough that Coffer should
  follow.

Treat changes as PRs against this doc, the same way ADRs evolve.
