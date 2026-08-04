# Investment transaction editor (A4.c.3)

Implementation surface for ADR-0029. The editor is mounted on the
investment register's strategy via `RegisterStrategy.Editor` —
brokerage rows route here; bank-shape rows continue to use
`../TxnRowEdit.tsx`.

## Folder layout

```
investment-edit/
├── README.md                     this file
├── InvestmentTxnRowEdit.tsx      orchestrator
├── actionLayout.ts               ACTION × field matrix (data, not switches)
├── validation.ts                 pure per-action validation
├── fields/                       reusable per-field widgets
│   ├── SecurityField.tsx
│   ├── SharesField.tsx
│   ├── PriceField.tsx
│   ├── AmountField.tsx
│   ├── CategoryField.tsx
│   ├── TransferField.tsx
│   └── FeeField.tsx
└── hooks/
    └── useInvestmentTxnDraft.ts  draft state + dirty / save plumbing
```

API plumbing lives at `src/Web/src/lib/api/investment.ts`
(POST / PATCH / DELETE per ADR-0029 endpoint surface, ADR-0030
domain-pure split). Re-exported from `@/lib/api`.

## Design choices (locked 2026-05-21)

### 1. The ADR-0029 matrix is data, not switch statements

`actionLayout.ts` exports `ACTION_LAYOUTS: Record<LedgerAction, FieldKey[]>`
— one row per action listing the field keys that show. The orchestrator
iterates the array and renders the corresponding field component for
each key.

**Why:** adding a future action becomes a one-row addition. Changing
a field becomes a single file edit. The action × field matrix in the
ADR matches the data structure 1:1; no scattered `switch(action)`
ifs to keep in sync.

### 2. Per-field components, not per-action components

Field widgets (`SecurityField`, `SharesField`, etc.) are reusable
controlled inputs. There are NO `<BuyEditor>` / `<SellEditor>` /
... shell components — each action is just a different selection of
the same field widgets, composed by the orchestrator's data-driven
render loop.

**Why:** 9 per-action shells would be 9 copies of the same input
boilerplate. Per-field components mean one input, one validation
rule, one place to fix a bug.

### 3. Reusing the existing `<Typeahead>` primitive

The bank editor uses `src/Web/src/components/ui/Typeahead.tsx` for
its category typeahead. The investment editor's `CategoryField`,
`TransferField`, and `FeeField` (category half) all consume the same
`<Typeahead>` with pre-filtered `items` lists (income / expense /
bank-shape / etc. — filter logic is colocated with each field
component, not in the picker).

**Why:** the primitive already exists and is well-tested. A wrapping
`<AccountPicker>` would be ceremony without value — three lines of
`<Typeahead items={filtered} … />` is clearer than a wrapper that
hides a one-line filter. Per `feedback_use_project_stack_for_tooling`:
extend what's there rather than add a new abstraction.

### 4. `useState` over `react-hook-form`

State lives in `useInvestmentTxnDraft` (a `useState`-based hook
lifted into the orchestrator). Fields are controlled components
with explicit `value` + `onChange` props. No form library.

**Tradeoff considered:**
- `react-hook-form` would give us fewer re-renders, built-in field
  errors, and less per-input boilerplate.
- `useState` is consistent with the existing bank editor, keeps
  state explicit ("what's in `useState` IS the truth"), adds no
  dependency, and keeps field components portable (controlled
  `value`/`onChange` works in any context, not just inside this
  form).

**Decision:** the form is small (≤7 fields per action). Re-render
perf is invisible at this scale; consistency with the bank editor +
explicit-over-magic posture (per `feedback_frontend_engineering_posture`)
matter more. If a much larger form ships later, that's the time to
introduce `react-hook-form`.

### 5. Validation is a pure function

`validation.ts` exports `validate(action, draft): Record<FieldKey, string | null>`
— a pure function. No React deps. Drives both the per-field error
display AND the save-button disable check.

**Why:** the action × field matrix's required-field rules can be
encoded once. Pure-function design means we can unit-test the rules
without rendering anything; the same function could later run
server-side for double-defence if needed.

### 6. Edit mode loads the full leg set from the `/legs` endpoint

The register window now returns **collapsed** investment events (ADR-0080:
the server-side `InvestmentEventProjector` does the aggregation, and
`InvestmentRow` carries the synthesized `categoryAccountId` /
`transferAccountId` / `feeAmount` / etc. slots directly). The raw per-leg
detail the editor needs — including the off-account income / transfer / fee
legs — is therefore fetched on demand from
`GET /transactions/{headerId}/legs` (`fetchHeaderLegs`) and inverted into a
draft via `legsToDraft`. Duplicate and "Create reminder" reuse the same fetch
+ inversion (cached under `['header-legs']`).

**Why:** one authoritative leg source (all accounts), shared by edit,
Duplicate, and the raw-data modal — no reliance on the register window,
which no longer carries raw legs.

## Out of scope here

- **FIFO consumption preview popover** — ships in A4.c.4 (`GET .../lots`
  endpoint is already in main).
- **Lot-edit affordance** — A5 (per ADR-0029's parked list).
- **Manual security inline add** — reuses the existing A3 securities
  modal; the editor's security picker just opens it.
