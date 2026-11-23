# `bank-edit/` — the bank transaction editor

The pieces of `TxnRowEdit.tsx`, mirroring `investment-edit/` next door: **pure
`.ts` at the root, `fields/` for widgets, `hooks/` for the draft hook.** The
extension is the boundary marker — a reviewer can tell what may import React by
looking at the filename, and the pure tier is what stays unit-testable.

`TxnRowEdit.test.tsx` **and** `TxnRowEdit.characterisation.test.tsx` pin the
editor's behaviour. A refactor here passes them with **no assertion edits**; a
change to one of their assertions is a deliberate behaviour change, and the diff
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
so the logic is testable on its own and the consolidation is a separate step.

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
**Why:** leg rows on the register's eight tracks, each field under its root
counterpart, buy vertical alignment and pay for it three times: columns 1–3 are
empty on every leg of every split, the category picker has to share one track
with the per-posting tags slot, and a leg row is therefore ~62px tall.
Twenty-five of those is ~1550px of form — the Save button is further away the
further down the split you work, and the running total scrolls off the top of
the screen while you type the amounts it sums. Legs have no register counterpart
to align with, so stepping out of the template costs nothing and buys back three
tracks of width. On its own grid a leg row is 30px, which is what makes a fixed-height
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
will not — and a highlight on the target ROW cannot express "between 2 and 3".

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
internally consistent.

**Why not the INVESTMENT pairing,** though red is also the error colour in this
grid: this is the BANK editor. It opens inside the bank register and visually
replaces bank rows, so under the other pairing every leg of a paycheck would be
red in the register and plain the instant you opened it — the same number
contradicting itself across two views, which is the exact failure two
representations are feared for. Nor is the collision real: a defect here is
signalled by a tinted row, an inset bar, a red field BORDER, a red row number
and a marker icon, none of which is a figure's text colour. The single-posting
branch uses the same pairing, so a plain transaction does not change colour
merely by being opened.

**9 · The second amount is a rule about one posting, decided by one predicate.**
*2026-11-16.* A posting between real accounts in different currencies carries the
other account's own figure (ADR-0114 D13, D14): `otherAmount` on the draft, shown as
Received or Sent right of Amount (`fields/SecondAmountField.tsx`), or under the split
column caption Other amount while a split's lines go both ways, required and nonzero
(`validation.ts`, kind `second`), sent as `counterpartyAmount` signed opposite to Amount
(`secondAmount.ts`). Its refusal is the field's own sentence, numbered by posting on a
split line, and the splits strip groups those lines by that sentence (ADR-0114 D14).
**Why it is not a balance rule:** it compares nothing across postings. It says one
posting's two legs are both there, as a blank amount does, so "no balance rule, ever"
below still holds, and zero stays legal for Amount.
**Why category legs are excluded:** a category holds no money of its own, so its leg
mirrors the paying side. The predicate (`secondAmountSide`) is: the source is not a
category, the counterparty is not a category, and their currencies differ. It is decided
once in the shell beside `counterpartyKind` and handed down. A bare currency comparison
would ask for a second amount on a category's own register, where the source is the
category and the editor's currency is the paying account's, and from a foreign-currency
source to a category.
**Why the field holds a magnitude:** its sign is always opposite Amount's, so a typed
minus is ignored and flipping Amount flips the saved figure. A stored or copied figure is
seeded `otherFixed` and never refilled from a rate; any other field follows Amount and
the date through the derived rate (`lib/useSecondAmount.ts`) until it is typed in.
Changing the counterparty drops the figure (`withCounterparty`), so one that comes back
is prefilled afresh.

## Out of scope

- **No balance rule, ever.** ADR-0025 explicitly rejected auto-balance and sum
  warnings. The running total in the grid's footer is informational; an
  "unbalanced" split is not an invalid one. `validation.test.ts` and
  `fields/SplitsGrid.test.tsx` both pin this so a later change cannot quietly
  introduce one.
- **Zero amounts stay legal.** Paycheck splits carry zero-amount lines and the database
  has no non-zero constraint.
- **Per-leg tags are real.** ADR-0103 gave a posting its own tags, distinct from
  the header's — a header tag is a claim about the transaction, a leg tag about
  one posting, and neither is derived from the other. On a one-split each side
  holds its own, and a side with none shows the other side's, inherited
  (ADR-0108). The slot in col2 is a
  fixed width; `TagSelect` fills it in its `compact` form — one control on
  every tag surface, because chips wrap and a 30px leg row cannot grow (ADR-0103 D7).
  The draft sends memo and tags by PRESENCE (`notesBody.ts`): an edit sends a
  key only when the user changed that value from what the editor opened with,
  because the server reads an omitted key as "leave it" and `null` / `[]` as
  "clear it", which on a one-split means "inherit the other side's". What the
  editor shows as inherited is what the save will leave (`inheritedAfterSave.ts`):
  from the counterparty in the draft, since a kept posting's other leg moves
  there with its values, and nothing once the stored posting is replaced. A create
  sends every value that is not blank, a save after collapsing a split to one
  line always sends memo and tags, and a reminder template edit sends every
  line's values in full.
- **The shell stays large.** So does the investment shell after full
  decomposition, and that is correct: the target is a shell holding only what no
  field can own. Driving it toward zero pushes queries into field components or
  inverts the dependency graph.

## Two things that must NOT move

1. **The container ref and the outside-pointerdown effect stay in the same
   component.** Separated, the ref is empty when the effect asks
   `happenedInside(event, containerRef.current)`, every press reads as outside,
   and the editor calls `onCancel()` on its own Save click — and the
   existing gate passes `cancelOnOutsideClick={false}`, so nothing in the repo
   would catch it. The row menu is `position: fixed` but NOT portalled, so it
   stays inside that ref and a click on a menu item does not cancel the edit.

2. **The similar-payees query stays in the shell**, because `editSingleHeaderId`
   derives from the *seed* posting count while the panel renders off the *live*
   count. A panel-owned query would tear down and refetch the moment the user
   clicks "Split this transaction →".
