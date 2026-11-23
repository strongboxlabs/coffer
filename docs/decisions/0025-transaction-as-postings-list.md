# 0025 — Transaction editing as a unified postings list

* Status: Accepted (amended 2026-10-27 by [ADR-0108](0108-memo-and-tags-per-side.md): memo and tags travel in `notes`, postings carry shape only; amended 2026-11-03: the editor in a dialog has its own layout; amended 2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md): a posting between real accounts in different currencies carries a required second amount and does not sum to zero, and the editors show it in one extra column or track)
* Date: 2026-05-15
* Companion to: [ADR-0019](0019-symmetric-postings.md), [ADR-0022](0022-txn-headers-and-legs.md), [ADR-0023](0023-ui-ux-interaction-conventions.md)

## Context

ADR-0022's schema makes a transaction = one `txn_headers` row + N
postings × 2 `txn_legs` rows. A "single-row" transaction is just
`N=1`; a "multi-split" is `N>1`. The schema treats them
identically — `posting_index` numbers the postings, and the
sum-to-zero CHECK applies per posting, not across the transaction.
*(Amended 2026-11-14, see the amendment of that date: no CHECK enforces the sum, and a
posting between real accounts in different currencies does not sum to zero.)*

The pre-ADR-0025 SPA and API surface didn't model that uniformity:

- `POST /transactions` accepted only a single `(accountId,
  counterpartyAccountId, amount)` triple — no way to create a
  multi-split.
- `PATCH /transactions/{id}` accepted `legEdits` (per-leg field
  tweaks on existing legs) but no add/remove postings, no reorder.
- No endpoint converted single ↔ split.

A "Convert to split" UI as a *separate flow* — right-click menu
item, separate modal, separate API endpoint — duplicates work:

- "Convert to split" and "edit split" share ~90% of their UI.
- "New transaction with N>1 postings" needs the same editor.
- "Convert split → single" is just "edit split with all but one
  posting removed" — no separate operation needed.

Building three (or four) overlapping flows when the schema is
already unified would have created divergent code paths for
operations that are conceptually one.

## Decision

**A transaction's mutable shape is `(header_fields, postings[])`.**
Every create / edit / convert operation is the same: replace the
postings list with the desired contents. The API exposes this
directly; the SPA renders one editor.

### API shape

```
POST /api/ledgers/{ledgerId}/transactions
{
  "postedAt": "...", "payee": "...", "memo": "...", "transactedAt": "...",
  "sourceAccountId": "<uuid>",
  "postings": [
    { "counterpartyAccountId": "<uuid>", "amount": <signed amount>, "legMemo": "..." },
    { "counterpartyAccountId": "<uuid>", "amount": <signed amount>, "legMemo": "..." }
  ]
}
```

`POST` requires `postings.length >= 1`. `length === 1` produces a
single-row; `length > 1` produces a multi-split.

```
PATCH /api/ledgers/{ledgerId}/transactions/{headerId}
{
  "payee": "...", "memo": "...", "postedAt": "...", "transactedAt": "...",
  "postings": {
    "sourceAccountId": "<uuid>",
    "items": [
      { "legId": "<uuid|null>", "counterpartyAccountId": "...", "amount": <signed amount>, "legMemo": "..." },
      ...
    ]
  }
}
```

When `postings` is supplied, the PATCH reconciles the existing
legs to match the requested list:

- `legId` present + matches an existing source-side leg → that
  posting is preserved (counterparty / amount / memo updated as
  requested). *(2026-10-27: counterparty and amount only; a line's
  memo and tags are addressed by index in `notes.lines`, see the
  amendment of that date.)*
- `legId` missing or null → new posting.
- Existing source-side legs whose `legId` is not referenced by
  any request item → that posting is deleted (both legs).
- `posting_index` is re-numbered from 0 in the order of
  `items[]` so a SPA-side drag-reorder maps directly to the
  schema.

When `postings` is omitted, header fields update in place and the
postings list is untouched — that's how a payee rename still
costs one PATCH with no postings overhead.

### Validation (one set, both endpoints)

- `postings.length >= 1`.
- No `amount === 0` (silently meaningless posting). *(Superseded: zero amounts are
  legal, as the 2026-09-16 amendment states, except on a posting between real accounts
  in different currencies, where both amounts must be nonzero (2026-11-14 amendment).)*
- `counterpartyAccountId !== sourceAccountId` per posting
  (`transaction-posting-self`).
- Every `counterpartyAccountId` lives in the same ledger
  (existing `account-not-in-ledger` carries this).
- For PATCH only: every `legId` that's provided must refer to an
  existing source-side leg of *this* header
  (`transaction-posting-leg-not-in-header`).

**Notably absent:** no "sum of postings must match a reference
amount." The sum-to-zero invariant is per-posting (the schema
CHECK enforces it via the source/counterparty pair). The
transaction's total = sum of source-side amounts, free-form.
Splitting a row into postings whose sum differs from the row's
original total is allowed — the user might be correcting both the
breakdown and the total in one edit. The SPA shows `Total: -$X.XX` as informational only.
*(Amended 2026-11-14, see the amendment of that date: per-posting sum-to-zero does not
hold between real accounts in different currencies, and no CHECK enforces it.)*

### Frontend

`TxnRowEdit` becomes one editor for every mutation:

- **Create new transaction:** start with one empty posting row.
- **Edit single-row:** load the one existing posting, pre-filled
  with its `legId`.
- **Edit multi-split:** load all postings.
- **Convert single → split:** edit single, add postings, save.
- **Convert split → single:** edit split, remove all but one
  posting, save.

Per-posting affordances:

- `⋮` left-edge drag handle — reorder. HTML5 native drag; no
  library dep.
- `[−]` right-edge remove. Disabled when only one posting
  remains.

Add-posting affordance: **a ghost row at the bottom of the
postings list** (per ADR-0023's modern-web pattern; matches
spreadsheets, Notion, Airtable, Linear). The ghost is rendered
faintly with `Add another posting…` placeholder text. Clicking
into any field — or tabbing off the last field of the last real
posting — materialises it as a real row and adds a new ghost
below. No explicit `[+ Add posting]` button anywhere.

`Total: $X.XX` readout below the postings list, refreshed live.
`[Cancel] [Save]` to commit; save fires PATCH (with the postings
reconcile) or POST (single shot).

## Amendment, 2026-09-16 — the splits editor gets its own grid

The postings-list MODEL above is unchanged and still exactly right: a
transaction is a list of postings, N=1 is not a special case, and there is no
convert-to-split path. What changed is how the list is drawn, and FOUR of the
per-posting affordances specified above are now wrong. They are restated here
rather than edited in place, so the reasoning that produced them stays legible.

**The legs left the register's grid.** Leg rows used to ride the register's
eight-column template so each field sat under its root counterpart. Columns 1-3
were empty on every leg of every split, which forced the category picker to
share one track with the per-posting tags slot and made a leg row ~62px tall. At the twenty-five splits this
editor is meant to handle that is ~1550px of form: the Save button recedes as
you work down the split, and the running total scrolls off the top of the
screen while you type the amounts it sums. The legs now have their own five
columns — `# · Category+Tags · Memo · Amount · actions` — which fits a leg in
30px and makes a fixed-height scroll viewport practical. The ROOT row still
rides the register template, so the transaction's own Date / Payee / Amount stay
aligned with the register behind it. See
`src/Web/src/routes/ledgers/bank-edit/README.md` for the full reasoning.

**The ghost row is gone, and with it "No explicit `[+ Add posting]` button
anywhere."** That sentence above is now reversed, deliberately. The ghost row
was the last element of the postings list, and once the list scrolls inside a
fixed-height viewport the affordance for adding a posting scrolls away with it
— on a many-leg paycheck you would scroll to the bottom every time you wanted
one more. Add now lives in the region's sticky footer, next to the running
total, reachable at any scroll offset. The ghost-row pattern the original
decision cites (spreadsheets, Notion, Airtable, Linear) assumes a list that
grows the page; it does not survive a viewport.

**The `⋮` drag handle is no longer at the left edge.** It sat in column 4, the
register's CHECK# track, because the legs rode the register template. It now
sits at the far right of the leg row, in the actions cell beside the defect
marker and the row menu. It is the only per-posting affordance above that this
amendment does not otherwise reverse, and therefore the one a reader would most
reasonably have taken as still current.

**`[−]` remove moved into a per-row menu**, alongside Move up / Move down. The
actions column is 3.5rem and cannot hold three controls; the menu is also the
visible path ADR-0021 Rule 10 prescribes, and it is what let reorder stop being
drag-only.

**Reorder is no longer drag-only.** HTML5 native drag stays — still no library
— but there is no drag on touch and none at all from a keyboard, so Move up /
Move down (Alt+↑ / Alt+↓) carry the same operation. The two paths use different
mutators on purpose: a drop splices the row out and re-inserts it at the target
index, which reads as "after you" dragging down and "before you" dragging up,
and is therefore NOT "exactly one slot" in both directions. The keyboard path is
a swap, which is.

**Still true, and load-bearing:** no balance rule. The running total in the
footer is informational, zero amounts are legal, and an "unbalanced" split is
not an invalid one. *(Amended 2026-11-14, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D13: except on a posting
between real accounts in different currencies, where both amounts must be nonzero.)* Two test files pin the absence so a later change cannot
quietly introduce one.

## Amendment, 2026-10-27 — memo and tags travel in `notes` (ADR-0108 D14)

**The API shape above is out of date in its memo fields.** Memo and tags are per side of
a transaction (ADR-0108 D1), so they left the header and the postings. The top-level
`"memo"` and each posting's `"legMemo"` are gone, with no compatibility shim. A posting
carries shape only (`legId`, `counterpartyAccountId`, `amount`), and the memo and tags
travel in a `notes` member of the `POST` and `PATCH` bodies (`TransactionNotesRequest`):

```
"notes": {
  "side": "<uuid>",
  "memo": "...", "tags": ["..."],
  "lines": [ { "index": 0, "memo": "...", "tags": ["..."] } ]
}
```

- `side` is the register the write is made from. It must equal the postings'
  `sourceAccountId` (`side-not-on-transaction`), and on a split it becomes the
  transaction's originating account.
- `memo` and `tags` are that side's own on a one-posting result and the whole
  transaction's on a multi-posting one. **Presence decides**, as for the header fields:
  an omitted key leaves the value, and `null`, `""` or `[]` clears it, which on a
  one-split means "show the other side's".
- `lines` addresses a line by its position in the postings sent. A posting kept by
  `legId` keeps its counterparty and amount as requested; its memo and tags are
  changed only through its entry in `notes.lines`, so a client echoing back the
  postings it read cannot strip them by omission.

**Validation gains:**

- `lines` on a one-posting result is refused (`line-notes-on-one-posting`), and so is a
  line that names no line of the originating account, names one twice, or addresses an
  investment event (`line-notes-invalid`).
- A tag set is refused only when the write GROWS it past the per-set limit; an
  unchanged or shrinking set saves even when a system merge left it over the limit
  (ADR-0108 D12).
- On an edit, an account already on the transaction is kept even if it has since been
  deactivated, and a newly added inactive account is refused (`account-inactive`); a
  create stays strict.
- A requested date on the stored calendar day keeps the stored posted timestamp, time
  of day included; only a different day changes it (`HeaderDates`).

**A change of shape moves the notes** (ADR-0108 D4). The postings list still has no convert-to-split
operation, but the write primitive (`AnnotationPlanner.Plan`) applies the shape's
transition before the request's notes: splitting a one-split copies what the writing
side showed onto the header and onto line 1; collapsing to one line joins the header's
memo with the line's (" · ", when they differ), unions their tags, and clears the
header. The request's values are then laid over that result, so the editor sends the
joined memo and merged tags it showed before Save.

## Amendment, 2026-11-03 — the editor in a dialog has its own layout

The 2026-09-16 amendment says the root row rides the register template. That holds in
the register. The editor is also hosted in dialogs: the reminder occurrence's Post or
skip, the reminder series editor and the home screen's Review. There is no register
behind a dialog to align with, so the register's own tracks (selection, status,
balance) were dead width there. In a dialog the editor takes `layout="dialog"`:

- **Wide:** five columns of its own, Date · Check # · Payee · Category · Amount, with
  Tax date, Memo and Tags beneath Date, Payee and Category, and the Similar and Merge
  strips under Payee, as in the register.
- **Narrow:** below `@3xl/dialog`, a container query on the dialog card
  (`@container/dialog`, `components/ui/EditorDialog.tsx`), one field per row: Date
  beside Check #, then Payee, the strips, Category, Amount, Tax date, Memo and Tags.
  The fields grow to 40px and the footer's Cancel and primary become 44px buttons that
  share the width. The fields learn their size from `FieldSizeContext`
  (`components/ui/fieldSize.ts`), which only the dialog layout sets.
- **The legs keep the splits grid** in both, at its own sizes. Where the dialog is
  narrower than its columns, its scrollport scrolls sideways and the dialog does not.

The investment editor follows the same principle: its form rows fill the dialog, and
below `@3xl/dialog` each field takes its own row at touch size, with the same footer.
The model and the save path are unchanged; only the drawing differs.

## Amendment, 2026-11-14 — a posting between currencies carries both amounts ([ADR-0114](0114-benchmarks-and-currency-conversion.md) D13, D14)

**The sum-to-zero invariant has one exception, and no CHECK, trigger or constraint
enforces a posting's sum** (ADR-0032). A posting between two real accounts in different
currencies records both amounts, each in its own account's currency, with opposite,
nonzero signs, and does not sum to zero ([ADR-0022](0022-txn-headers-and-legs.md) Rule 2, 2026-11-14 amendment).
Every other posting, a category leg's included, still sums to zero. The transaction's
total is still the sum of the source-side amounts.

**The posting shape gains the second amount**, the counterparty side's own figure, as a
new field `counterpartyAmount` beside `counterpartyAccountId`. It is present only while
the counterparty is a real account in another currency than the source; switching the
counterparty to a category or to a same-currency account drops it.

**Validation gains one rule:** on a posting between real accounts in different
currencies, the second amount is required. A missing, zero or same-signed second amount
is refused. Zero amounts stay legal on every other posting (2026-09-16 amendment).

**The editors show it in one extra place**, right of Amount, only while it applies:

- **The splits grid** gains one column right of Amount, with a header caption, for a line
  whose counterparty is a real account in another currency. The five columns of the
  2026-09-16 amendment become six while it shows. Its caption is Received, Sent, or
  Other amount while the lines go both ways (ADR-0114 D14). The footer total sums the
  Amount column only, in the register account's currency, never the second amount's
  column, as it does today.
- **The dialog layout** gains one track while it shows, in every host of it: Post or
  skip…, the reminder series editor and the home screen's Review…. That track is a sixth
  column right of Amount on a wide card, and a row after Amount below `@3xl/dialog`.
- **The investment editor** places it right of Amount, in a row of its own below
  `@3xl/dialog`. BuyXfr, SellXfr and DividendXfr pair it with the trade amount, not the
  fee.

## Consequences

**Positive**

- Schema, API, and UI all model the same shape: a transaction is
  a postings list.
- No "convert-to-split" code path. No "convert-to-single" code
  path. Both fall out of the same edit operation. *(2026-10-27: still
  true of the postings; the memo and tags do have a transition on a
  change of shape, applied by the write primitive, see the amendment
  of that date.)*
- The SPA's `TxnRowEdit` is one state shape and one save handler —
  used by `+ New transaction`, double-click-row edit, Enter on a
  focused row, and the splits editor. *(2026-09-16: no longer one
  COMPONENT. It is a shell over `bank-edit/` — the pure tier, the
  draft hook and the field components. It still has one state shape
  and one save handler.)*
- Reorder (drag) is structurally trivial: the order of `items[]`
  in the request maps to `posting_index`, no separate "move
  posting" endpoint.
- Posting list extensions (per-posting tags, per-posting
  status?) land on a single shape rather than three diverging
  ones.

**Negative**

- `CreateTransactionRequest` breaks from its pre-0025 shape
  (`accountId + counterpartyAccountId + amount` → `sourceAccountId
  + postings[]`). The SPA is the only caller; the change is
  coordinated in the same PR. No external API consumers.
- `PatchTransactionRequest.legEdits` is retired. The pre-0025
  shape supported per-leg field tweaks but not add/remove/reorder
  — `postings.items[]` is a superset. Same migration risk
  surface as above.
- The PATCH endpoint is now noticeably more complex on the
  server side: a reconcile loop that classifies each existing
  leg as keep/update/delete and each request item as
  update/insert. The complexity is intrinsic to the operation,
  not artifact — pre-0025 the same complexity was distributed
  across three would-be endpoints.

**Breaking**

- `POST /transactions` body shape change. SPA migrated in lockstep.
- `PATCH /transactions/{id}` body shape change — `legEdits` →
  `postings`. SPA migrated in lockstep.
- Integration test fixtures using the old shapes get rewritten.

## Alternatives considered

- **Separate `convert-to-split` endpoint + separate `edit-split`
  endpoint.** Two endpoints, two SPA flows, two validation
  surfaces. Rejected as the original roadmap design once the
  schema-level unification became obvious.
- **Keep `legEdits` for the in-place per-leg case, add a separate
  `reshape-postings` endpoint for add/remove/reorder.** Two
  PATCH-adjacent endpoints, still a divergence the schema doesn't
  need. Rejected for the same reason.
- **Always require `sum(postings) === reference`.** Constrains
  the user from correcting both breakdown and total in one edit.
  Rejected — the schema doesn't constrain it and the UX is
  already explicit (the live `Total:` readout makes the user's
  effective change visible).

## Open questions

None. Auto-balance / sum-constraint warnings were the only open
UX question; the design call (no auto-balance, no warnings) is
captured above.
