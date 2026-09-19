# `bank-edit/` — the bank transaction editor

`TxnRowEdit.tsx` was 1,643 lines and its split branch grew without bound. This
folder is where its pieces went, mirroring `investment-edit/` next door: **pure
`.ts` at the root, `fields/` for widgets, `hooks/` for the draft hook.** The
extension is the boundary marker — a reviewer can tell what may import React by
looking at the filename, and the pure tier is what stays unit-testable.

The shell is now ~980 lines, down from 1,643 — in the same range as the
investment shell next door.

*Approximate on purpose.* An exact count here has already been wrong twice: it
was written as 989 (true mid-decomposition, before the redesign cut it), then
corrected to 983, then stale again the same day because a comment was deleted.
A figure that moves with every edit invites a reader to spot-check it and find
the document lying about something trivial. `wc -l` is the source of truth; this
line is for the order of magnitude.

Two pieces of work landed here: the decomposition (Follow-ups Slice 3), which
was behaviour-zero by rule, and the splits redesign, which was not. The
decomposition's gate was that `TxnRowEdit.test.tsx` **and**
`TxnRowEdit.characterisation.test.tsx` pass with **no assertion edits**, and it
held. The redesign then changed three of those assertions on purpose; the diff
is the record of exactly what changed, which is what a characterisation suite
is for.

## Design choices

**1 · The inverters take structural inputs, not the register DTOs.**
*2026-09-15.* `seedToDraft` accepts `PostingSeedLike`, not `TxnRowEdit`'s
exported `PostingSeed`.
**Why:** importing the DTO would make a cycle, since the shell imports this
module. It is also what let the investment editor's inverter be reused by
`reminderOccurrenceDraft` with no cast — a DTO-typed inverter forces filler
objects at every non-register call site. The shell's DTOs satisfy these
structurally, so no call site changed.

**2 · `validatePostings` returns `readonly string[]`, NOT a keyed map.**
*2026-09-15.* A deliberate deviation from `investment-edit/validation.ts`,
which returns `Partial<Record<Key, string>>`.
**Why:** the messages are index-prefixed (`Posting 2: amount is required.`) and
rendered verbatim as joined text — `join(' · ')` in the single-posting warning
strip, `join('\n')` in the Save button's title. One posting can also contribute
*two* messages (blank amount **and** missing counterparty), which a field-keyed
map collapses into one while losing construction order. Converting is a
user-visible text change, not a refactor.

`postingIssues` is the same rules in structured form — one entry per defect,
carrying the posting's key, its row number, the kind and a short hint — and
`validatePostings` is built from it. **One rule set behind both shapes**, because
a row marking itself red while the Save tooltip disagrees about why is exactly
what two parallel validators produce.

**3 · The seeds are per-field, not one `modeToDraft`.**
*2026-09-15.* `draft.ts` exports `seedPayee`, `seedMemo`, `seedCheckNumber`,
`seedPostedAt`, `seedTransactedAt`, `seedTags`, `seedPostings`, and
`hooks/useTxnRowDraft.ts` calls them one per `useState`.
**Why:** one constructor returning the whole draft is the right end state, but
it means touching every reference to seven state variables in the SAME diff
that moves the seeding logic — two risks at once, with the gate only able to
say that something broke, not which. Per-field seeds need no call-site changes,
so the logic became testable first and the consolidation stayed a separate step.

**4 · `nextKey` is a module counter and deliberately not pure.**
*2026-09-15.*
**Why:** `key` is the React key for a leg row and must be stable across a
reorder, so it cannot derive from array position or from `legId` (a freshly
added posting has none). The consequence is that draft construction is **not
idempotent**, which is why `useTxnRowDraft` captures its initial state with
`useState` rather than `useMemo` — a recomputed initial carries different keys
from the live draft, which remounts every leg row and destroys focus and any
in-flight edit.

**5 · The legs have their OWN grid; only the root row rides the register's.**
*2026-09-15.* `fields/SplitsGrid.tsx` lays legs out on
`# · Category+Tags · Memo · Amount · actions`.
**Why:** leg rows used to ride the register's eight tracks so each field sat
under its root counterpart. That bought vertical alignment and paid for it three
times: columns 1–3 were empty on every leg of every split, the category picker
had to share one track with the per-posting tags slot, and a leg row was
therefore ~62px tall.
Twenty-five of those is ~1,550px of form — the Save button is further away the
further down the split you work, and the running total scrolls off the top of
the screen while you type the amounts it sums. Legs have no register counterpart
to align with, so stepping out of the template costs nothing and buys back three
tracks of width. A leg row is now 30px, which is what makes a fixed-height
scroll viewport practical: **the list scrolls, the editor does not grow.**

**6 · Reorder works from a keyboard and on touch, not by drag alone.**
*2026-09-15.* Move up / Move down in the row menu with Alt+↑ / Alt+↓, alongside
the drag handle.
**Why:** there is no drag on touch and none at all from a keyboard, so a
drag-only reorder was unreachable for anyone not using a mouse. The two paths
use *different* mutators and that is deliberate: `reorderPostings` splices the
row out and re-inserts it at the target index, so a drop means "after you" when
dragging down and "before you" when dragging up — correct for a drag, and not
"exactly one slot" in both directions. `movePosting` is a swap, which is.

The drag's insertion line is drawn from the same asymmetry rather than always
before the hovered row. Anything else would show the row landing somewhere it
will not, which is the defect the line was added to fix: the old drag
highlighted the target ROW, and a row highlight cannot express "between 2 and 3".

**7 · The per-leg memo stays a `<textarea>` even though the row is one line.**
*2026-09-15.*
**Why:** `<input>` runs the HTML value-sanitisation algorithm, which strips
newlines. An existing multi-line leg memo rendered in one would be silently
mangled and the next save would persist the mangling. Fixed height, no
auto-grow — the height budget is what keeps the scroll viewport predictable.

**8 · A debit is red, a credit is plain — the BANK register's pairing.**
*2026-09-16.* Follows `register/strategies/bankRowStrategy.tsx`, which uses it
on the main row, the split parent and the leg row alike.

The two registers deliberately differ: the bank register flags money going out
in red, the investment register flags money coming in green, and each is
internally consistent. This editor first followed the INVESTMENT pairing on the
reasoning that red is also the error colour in this grid, so a split of ordinary
expenses would read as twenty-five errors.

**Why that was wrong:** it is the BANK editor. It opens inside the bank register
and visually replaces bank rows, so every leg of a paycheck was red in the
register and plain the instant you opened it — the same number contradicting
itself across two views, which is the exact failure two representations are
feared for. The collision being avoided is also not real: a defect here is
signalled by a tinted row, an inset bar, a red field BORDER, a red row number
and a marker icon, none of which is a figure's text colour. The single-posting
branch carried no colour at all, so a plain transaction changed colour merely by
being opened; it now uses the same pairing.

## Out of scope

- **No balance rule, ever.** ADR-0025 explicitly rejected auto-balance and sum
  warnings. The running total in the grid's footer is informational; an
  "unbalanced" split is not an invalid one. `validation.test.ts` and
  `fields/SplitsGrid.test.tsx` both pin this so a later change cannot quietly
  introduce one.
- **Zero amounts stay legal.** Paycheck splits carry $0 lines and the database
  has no non-zero constraint.
- **Per-leg tags stay a placeholder.** ADR-0009 puts tags at header level. The
  compact `TagsPlaceholder` chip reserves the slot on purpose so a future tags
  PR does not reshuffle the form.
- **The shell stays large.** The investment shell is still 890 lines after full
  decomposition, and that is correct: the target is a shell holding only what no
  field can own. Driving it toward zero pushes queries into field components or
  inverts the dependency graph.

## Two things that must NOT move

Both verified against the code rather than assumed:

1. **The container ref and the outside-pointerdown effect stay in the same
   component.** Separated, `containerRef.current?.contains(...)` yields
   `undefined` and the editor calls `onCancel()` on its own Save click — and the
   existing gate passes `cancelOnOutsideClick={false}`, so nothing in the repo
   would catch it. The row menu is `position: fixed` but NOT portalled, so it
   stays inside that ref and a click on a menu item does not cancel the edit.

2. **The similar-payees query stays in the shell**, because `editSingleHeaderId`
   derives from the *seed* posting count while the panel renders off the *live*
   count. A panel-owned query would tear down and refetch the moment the user
   clicks "Split this transaction →".
