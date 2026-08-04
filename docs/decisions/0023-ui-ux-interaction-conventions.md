# 0023 — UI/UX interaction conventions and modern web defaults

* Status: Accepted
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
re-litigates those choices. The register inline-edit slice
(2026-05-12) made this concrete: shipped per-cell click-to-edit,
got redirected to row-level edit; shipped `[Save] [Cancel]`
(legacy desktop order), got redirected to `[Cancel] [Save]`
(modern web order); shipped a state-race between Typeahead
`onChange` + `onCommit` because the contract wasn't pinned. Two
implementation rounds and one schema-touching refactor before
the same edit form was right.

This ADR captures the conventions that came out of those
discussions so the next register surface, settings page, or modal
inherits them by default. The default is **modern web** rather
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

This matches Moneydance / Quicken muscle memory and avoids the
per-cell trap where each click commits independently. Single-
click on a row is reserved for **focus** (see §B.1 below) — a
distinct state from selection, which is checkbox-gated.

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
that aren't first-class enough to merit a dedicated toolbar button —
today: Duplicate, Show other side, Delete. Future actions (split,
attach receipt, …) land here too.

Menu items:

| Item | Behaviour | Disabled when |
|---|---|---|
| Duplicate | Opens the new-transaction form prefilled with the source row's payee / memo / amount / counterparty. `posted_at` defaults to today (the user usually wants the duplicate dated today). | — |
| Show other side | Navigates to the counterparty account's register with `?focus=<headerId>` so the receiving page scrolls + focuses the matching leg. | Counterparty account isn't visible to the user (RLS-filtered) or the row has no counterparty leg. |
| Delete | Confirms via modal, then sends `DELETE /transactions/{id}`. Server hard-deletes manual entries (`external_id IS NULL`) and soft-hides feed / import-keyed rows (`is_hidden=true`) so a re-source doesn't resurrect them. | — |

Keyboard inside the menu follows ADR-0023 §L (modal/popover
dismissal) plus the per-menu specifics:

| Key | Action |
|---|---|
| ArrowDown / ArrowUp | Move highlight (wraps; skips disabled items). |
| Enter / Space | Activate the highlighted item; menu closes. |
| Esc | Close (stops bubbling — parent's cancel handler must not also fire). |
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
(debounced ~200ms) so the readout stays accurate across window
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
  opacity.
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

`Total: $X.XX` readout below the postings list is
informational only — there's no sum-constraint warning (the
schema's invariant is per-posting, not transaction-wide).

### D — Keyboard inside edit forms

| Key | Action |
|---|---|
| Tab / Shift-Tab | Native browser focus order. Skips disabled inputs automatically. |
| Enter | **Saves the row.** Exception: inside a Typeahead with a highlighted suggestion, the first Enter picks the suggestion (`preventDefault`) and the second Enter (popover now closed) saves. |
| Esc | **Cancels the row.** Exception: inside a Typeahead with an open popover, the first Esc closes the popover; the next Esc cancels the row. |
| Ctrl/⌘+Enter | Always saves from anywhere — escape hatch when Enter is consumed by a child (textarea, future rich-text fields). |

All keyboard shortcuts surface via `title` tooltips on the
relevant buttons ("Save (Enter or ⌘+Enter)", "Cancel (Esc)") so
users see them on hover without needing this doc.

### E — Cancel semantics

**Cancel discards local edit state with no confirmation prompt.**
Modern web default. Triggers:

1. The Cancel button.
2. Esc (at form level, after the Typeahead has had its chance).
3. Click anywhere outside the edit container (sidebar, toolbar,
   table whitespace).
4. Switching to another row's edit (implicit cancel via the
   single-slot `editingId` state).

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
  picks + lets focus advance (no preventDefault); Esc closes
  popover (bubbles — parent form's Esc handler runs after).
- **Click outside** closes the popover without firing `onChange`
  (preserves user-typed value for the parent's Save to read).
- **Click on item** picks via `onChange` (same path as Enter on
  highlight).

The contract eliminates the React-setState-batching race where a
parent's commit handler would read a stale value because
`onChange(newValue)` and `onCommit()` ran in the same callback.

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
  `-$12.50` for negatives, `$50.00` for positives (no leading
  plus).
- Colour: `text-state-danger` for negatives, default `text-text`
  for positives. Green-for-positive (`text-state-success`) reads
  as "look at all this income!" — misleading on a register that's
  mostly spending.
- Right-aligned, decimal-aligned, `font-mono tabular-nums`.
- Scheduled rows: drop the danger colour (everything muted —
  the row's own muting carries the signal).

For data-entry inputs, accept a signed number in one field
(`type="number"` with `step="0.01"`). Don't split outflow/inflow
into two inputs.

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
  ("Save" → "Saving…") + `disabled={true}` on the button. Avoid
  replacing the entire button with a spinner — the label going
  to "Saving…" reads instantly; a bare spinner adds a "what is
  this" pause.
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

Decision data point: 2026-05-12 scan of the user's imported MD
ledger showed 7% of memos exceed one line of register width and
the long tail goes to ~400 chars. Memos are a real notes field
in practice, not a one-line tag.

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

**Auto-grow implementation:** hand-rolled in `onChange` —
reset height to `auto`, then set to `min(scrollHeight, max)`.
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

This is the operational instruction from
`feedback_modern_ux_conventions.md`: "give 2 options with
tradeoffs, pick the default, name it." UI/UX gets double the
rigour, not the same rigour — the cost of getting it wrong is a
visible regression in the user's daily flow.

### P — Field label casing + editor action bar (added 2026-06, ADR-0049 work)

- **Field labels are small all-caps:**
  `text-[0.625rem] font-semibold uppercase tracking-wider text-text-muted`.
  This is the de-facto standard across the app (the investment editor + its
  fields, the account/category picker, tables, the reminders calendar,
  securities, snapshots). The bank register inline editor (`TxnRowEdit`) was the
  lone title-case holdout and was harmonized onto it. Settings-form labels built
  on the `Label` primitive (`text-sm`) are a distinct, heavier context — exempt.
- **The editor action bar** (`[Cancel] [Save]`, per A) lives in a **bottom
  footer**, right-aligned, built with the shared `Button` primitive. The
  investment register editor previously placed its buttons top-right of its
  first row with hand-rolled `<button>`s; it was harmonized onto the
  bottom-footer convention so both register editors match. A host (e.g. the
  reminders occurrence dialog) may inject a left-aligned secondary action into
  the footer's leading slot.

## Consequences

**Positive**
- One reference for "how do I do X in Coffer's UI?" — onboarding
  any new surface starts here, not from re-derivation.
- Future inconsistencies become explicit: any UI that diverges
  needs to update this ADR (and explain why) instead of silently
  drifting.
- Memory entries (`feedback_modern_ux_conventions.md`,
  `feedback_architecture_first.md`) point here for the rules;
  the rules live in one place rather than scattered across
  feedback notes.

**Negative**
- Adds a document to maintain. When a new UI pattern ships, this
  doc gets a section appended — easy to forget.
- Some conventions (single Amount column, double-click to edit)
  diverge from MD; users coming from MD will notice. The
  divergence is intentional but adds a moment of "oh, this
  is different" friction.

## Alternatives considered

- **Inline conventions per ADR / per component.** Rejected:
  this is what we had until now, and it doesn't survive
  multiple surfaces being designed independently. Same trap that
  produced two-round register-edit slice.
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
