# 0025 — Transaction editing as a unified postings list

* Status: Accepted
* Date: 2026-05-15
* Companion to: [ADR-0019](0019-symmetric-postings.md), [ADR-0022](0022-txn-headers-and-legs.md), [ADR-0023](0023-ui-ux-interaction-conventions.md)

## Context

ADR-0022's schema makes a transaction = one `txn_headers` row + N
postings × 2 `txn_legs` rows. A "single-row" transaction is just
`N=1`; a "multi-split" is `N>1`. The schema treats them
identically — `posting_index` numbers the postings, and the
sum-to-zero CHECK applies per posting, not across the transaction.

The pre-ADR-0025 SPA and API surface didn't model that uniformity:

- `POST /transactions` accepted only a single `(accountId,
  counterpartyAccountId, amount)` triple — no way to create a
  multi-split.
- `PATCH /transactions/{id}` accepted `legEdits` (per-leg field
  tweaks on existing legs) but no add/remove postings, no reorder.
- No endpoint converted single ↔ split.

The roadmap had a "Convert to split UI" slice queued as a *separate
flow* — right-click menu item, separate modal, separate API
endpoint. Working through the design surfaced that:

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
    { "counterpartyAccountId": "<uuid>", "amount": -60.00, "legMemo": "..." },
    { "counterpartyAccountId": "<uuid>", "amount": -40.00, "legMemo": "..." }
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
      { "legId": "<uuid|null>", "counterpartyAccountId": "...", "amount": -60.00, "legMemo": "..." },
      ...
    ]
  }
}
```

When `postings` is supplied, the PATCH reconciles the existing
legs to match the requested list:

- `legId` present + matches an existing source-side leg → that
  posting is preserved (counterparty / amount / memo updated as
  requested).
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
- No `amount === 0` (silently meaningless posting).
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
Splitting a $100 row into $60 + $30 (total $90) is allowed — the
user might be correcting both the breakdown and the total in one
edit. The SPA shows `Total: -$X.XX` as informational only.

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

## Consequences

**Positive**

- Schema, API, and UI all model the same shape: a transaction is
  a postings list.
- No "convert-to-split" code path. No "convert-to-single" code
  path. Both fall out of the same edit operation.
- The SPA's `TxnRowEdit` is one component, one state shape, one
  save handler — used by `+ New transaction`, double-click-row
  edit, and (implicitly) by any future "edit splits"
  affordance.
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
captured above and matches user feedback during the 2026-05-15
design discussion.
