# 0029 — Investment transaction editor + endpoint surface

* Status: Accepted. **Extended by [ADR-0065](0065-transfer-shares-in-kind.md)** —
  the `transfer_shares` action adds a row to the action × field matrix (security +
  positive qty + destination investment account; no price/amount/fee).
* Date: 2026-05-21
* Related: ADR-0024 (bulk selection), ADR-0025 (transactions as
  postings list), ADR-0027 (action catalog), ADR-0028 (register
  surface)

## Context

ADR-0025 locks the surface for **bank** transaction create/edit:
one endpoint, one POST, one PATCH, postings list. That endpoint
can technically accept investment-shape txns (multi-posting under
one header), but the shape is non-trivial: paired postings,
holdings + lots side-effects, per-action validation, FIFO lot
consumption on sells. Funneling it through the bank-flavored
`CreateTransactionRequest` forces the API to re-classify on every
save and produces a noisy validation surface (every field becomes
optional, the API decides which are required based on action).

The register surface (ADR-0028) and the importer's three-source
classifier (ADR-0027) give us a clear contract for what an
investment txn IS; the editor + endpoint should make that
contract the API's input shape, not a post-hoc validation.

## Decision

### Action picker: 9 catalog entries (+ transfer_shares)

The user-facing action selector exposes the 9 ADR-0027
catalog actions: **Buy / BuyXfr / Sell / SellXfr / Div / DivReinvest /
DivXfr / Xfr / Misc** — plus **Transfer shares** (ADR-0065, in-kind move
to another investment account). Direction (income vs expense for Misc; in vs
out for Transfer) is **discriminated by amount sign**, not by
separate picker entries. Matches ADR-0027's data-model collapse.

### Action × field state matrix

Legend: ✓ required · ~ optional · 🔒 computed · — hidden

(`transfer_shares` row, ADR-0065: security ✓ · shares ✓ (positive, qty to move) ·
transfer ✓ (destination investment account) · price/amount/category/fee — hidden.)

| Field                | buy | buyx | sell | sellx | div_cash | div_reinv | divx | xfr | misc |
|----------------------|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| Date                 | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Check #              | ~ | ~ | ~ | ~ | ~ | ~ | ~ | ~ | ~ |
| Payee / Memo         | ~ | ~ | ~ | ~ | ~ | ~ | ~ | ~ | ~ |
| Status               | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Security             | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | ~ |
| Shares               | ✓ | ✓ | ✓ | ✓ | — | ✓ | — | — | — |
| Price                | ✓ | ✓ | ✓ | ✓ | — | ✓ | — | — | — |
| Category             | — | — | — | — | ✓ | ✓ | ✓ | — | ✓ |
| Transfer destination | — | ✓ | — | ✓ | — | — | ✓ | ✓ | — |
| Fee category         | ~ | ~ | ~ | ~ | ~ | ~ | ~ | — | ~ |
| Fee amount           | ~ | ~ | ~ | ~ | ~ | ~ | ~ | — | ~ |
| Amount (cash)        | 🔒 | 🔒 | 🔒 | 🔒 | ✓ | 🔒 | 🔒 | ✓ | ✓ |

### Endpoint: `/api/ledgers/{lid}/investment-transactions`

Distinct from `/transactions`. Same lifecycle verbs:

  - `POST` — create one investment txn (multi-posting body per
    action; server stamps `posting_role` per ADR-0028).
  - `PATCH /{headerId}` — edit; replaces postings wholesale per
    ADR-0025 reconcile rules.
  - `DELETE /{headerId}` — same hard-delete vs soft-hide policy
    as `/transactions`: hard-delete manual rows, soft-hide rows
    with `external_id` set. **Load-bearing for the queued
    SimpleFIN brokerage feed**, where re-sync against the bank
    could resurrect a hard-deleted txn.
  - `GET /api/ledgers/{lid}/accounts/{aid}/securities/{sid}/lots` —
    open lots for FIFO consumption preview (read-only, ordered
    `acquired_at` ASC).

`InvestmentTransactionsController` + `InvestmentTransactionsRepository`,
new in `src/Api/`. LINQ + EF only — no raw SQL in the data-access
layer (matches `feedback_no_raw_sql_in_api`). FIFO consumption +
holdings recompute on POST/PATCH happen via
`fn_recompute_holdings_cost_basis` (migration 056); the endpoint
inserts/replaces legs and triggers the recompute.

### Request shape

```typescript
interface CreateInvestmentTransactionRequest {
  brokerageAccountId: string;
  postedAt: string;                  // ISO-8601 UTC
  action: 'buy' | 'buyx' | 'sell' | 'sellx'
        | 'dividend_cash' | 'dividend_reinvest' | 'divx'
        | 'transfer' | 'misc';
  payee?: string | null;
  memo?: string | null;
  checkNumber?: string | null;
  // Action-driven content (validated against the matrix above):
  securityId?: string | null;        // required except 'transfer'
  shares?: number | null;            // required for buy/buyx/sell/sellx/divr
  price?: number | null;             // required for buy/buyx/sell/sellx/divr
  amount?: number | null;            // required for div_cash / transfer / misc
  categoryAccountId?: string | null; // required for div_*/divx/misc
  transferAccountId?: string | null; // required for buyx/sellx/divx/transfer
  feeAccountId?: string | null;      // optional except 'transfer'
  feeAmount?: number | null;         // required when feeAccountId set
}
```

PATCH mirrors POST but every field optional; null clears
overrides on managed rows. Server rejects shape violations with a
structured 422 envelope (matching the bank `/transactions` error
contract).

### Shared domain layer: `Coffer.Domain.Investment`

New project / namespace (placement at implementation time). Hosts
the posting-shape builders previously inside the importer:

  - `BuildSecPair(...)` — sec leg pair (brokerage cash ↔ Holdings
    sibling).
  - `BuildCategoryPair(...)` — income / fee leg pair (brokerage
    cash ↔ category).
  - `BuildXferPair(...)` — transfer leg pair (brokerage cash ↔
    external account).
  - `BuildHoldingsImpact(action, ...)` — returns `(HoldingDelta?,
    LotRow?)` for the action.

Both the importer and the new API endpoint consume the same
helpers. Splittype → posting_role mapping is centralized here.
The importer's existing `TransactionRow` intermediate type may
remain as an internal MD-classification scaffold during this
slice; a follow-up can collapse it once the shared layer is
bedded in.

### FIFO lot preview

Editor calls `GET .../lots?openOnly=true` when the user enters
`shares > 0` on a Sell / SellX. A popover (small affordance below
the shares field) shows which lots get consumed and the realized
gain. The preview is **advisory**: the server runs the actual
FIFO consumption on save, so the preview can drift from the saved
result if open lots change between preview and save (rare; editor
reopens the preview on input change).

### Fee + `is_trade_commission` interaction

The editor surfaces a small contextual hint near the fee input:

  - Brokerage's `is_trade_commission = TRUE`: *"This fee will be
    added to the lot's cost basis."*
  - `FALSE`: *"This fee will be booked as an expense; cost basis
    uses the share price only."*

The flag itself is set on the brokerage account settings (shipped
in A4.a); the editor does NOT expose a flip toggle.

### Editor component

`InvestmentTxnRowEdit` (new) plugged into `RegisterStrategy.Editor`
on `investmentStrategy`. Bank/credit/cash/asset/liability continue
to use `TxnRowEdit`. Both surfaces share inline-edit + new-row
entry patterns from ADR-0024 / ADR-0025.

> **Update (2026-06):** the `RegisterStrategy.Editor` indirection was
> removed (see ADR-0028 update). `InvestmentRegisterPage` now wires
> `InvestmentTxnRowEdit` directly and `BankRegisterPage` wires
> `TxnRowEdit` directly; the editor binding is no longer routed through a
> strategy object.

### Out of scope (parked)

  - **Edit Lots** (manual lot override) — A5.
  - **SimpleFIN brokerage feed** — queued slice. Will introduce
    sync-source investment txns with stable `external_id` +
    `online_match_fitid` keys, reusing this endpoint's create
    path internally.
  - **OFX file import for investment accounts** — queued slice.
    Direct OFX file ingestion (separate source from SimpleFIN,
    same endpoint downstream).
  - **CSV file import for investment accounts** — queued slice.
    Lowest-fidelity feed; per-institution column mapping saved +
    reused.
  - **Investment row matching / merging** — queued after all
    three feed sources. Brokerage-side equivalent of slice
    2c.6d's bank merge UI.
  - **Splitting / merging investment with non-investment txns** —
    separate, lower-priority slice.

### Amendment (2026-09): three parity corrections

The merge slice parked above has long since shipped. Reviewing it against
the bank editor turned up three places where the investment side had
quietly acquired a rule the bank side does not have. All three are
corrections toward bank, not new behaviour — the divergences were
accidents of how each surface grew, not decisions anyone recorded.

**1. Merge candidates constrain settledness only.** The bank rule is: any
accepted, un-merged, effectively-visible row on the same account, same
effective amount, within ±7 days. The investment query additionally
required a candidate to carry a SECURITY leg on the Holdings sibling,
with a non-null quantity and action — a SHAPE constraint on a rule about
settledness.

The consequence was that the commonest duplicate on a brokerage could
never be offered. A dividend the user already recorded as cash is
(brokerage cash, income category) with no holdings leg at all; a
feed-delivered `INVBANKTRAN` row is (brokerage cash, Uncategorized) with
no action either. The merge panel came up empty on exactly the rows it
exists for.

The amount match now runs on the brokerage cash leg, which is bank's rule
verbatim. The share match on the Holdings sibling is kept ALONGSIDE it,
because it covers a case bank has no analogue for: a reinvest moves no
cash, so an amount match can never see one. A candidate's ticker, share
count and price still come from its security leg when it has one, and are
null on the chip when it does not.

The `ingest_security_id` narrowing still applies — but only to candidates
that HAVE a security to contradict. A cash-shape row carries none
*because* it has not been classified yet, which is precisely what makes it
the duplicate worth offering.

**Direction is part of the rule.** Bank compares the SIGNED sum on the
source account; the investment query compared magnitudes — on the
security leg with `Math.Abs` before the widening, and on the brokerage leg
with the same absolute comparison after it, under a comment claiming
parity it did not have. So a sell that brought in $167.38 was offered the
buy that spent $167.38 on the same security the same day: the same
magnitude, the opposite event, and a merge that would tombstone a real
transaction and halve the position. Found on the dev rig immediately
after release.

A candidate whose cash moved the opposite way is now dropped. The check
is applied once over every match path, not inside the amount match,
because the share match can pair a sell with a buy by `|quantity|` just
as readily. It is skipped when the anchor moved no cash: a reinvest nets
to zero on the brokerage and so has no direction for a candidate to
contradict.

**2. One transaction per PATCH, opened before the first read.** The bank
`PatchAsync` opens its transaction as its first statement, and says why:
the ledger-membership guard must run inside the transaction that later
writes, or there is a TOCTOU window between the endpoint's cross-ledger
check and the writes. The investment `PatchAsync` opened two transactions
further down — one inside the merge branch, one after validation — so the
membership guard, the merge branch's eligibility checks and the whole
action × field matrix validation ran unprotected.

This has no single-threaded symptom, which is why every integration test
passed before and after. It is pinned by a source-level assertion
(`WriteTransactionBoundaryTests`) rather than a concurrency harness: a
test that can only be written as a race is a test that gets skipped.

**3. Similar-payees recall reaches the investment editor.** The endpoint
needed nothing: a feed row lands BANK-shape whatever account it is bound
for — two legs, (real account, Uncategorized), with the investment detail
parked in the `ingest_*` carriers until someone classifies it — so its
anchor resolves on a brokerage exactly as on a checking account. The
investment editor simply never called it, and a dividend categorised the
same way every quarter had to be categorised by hand every quarter.

Two rules make it fit this editor:

A suggestion is one (payee, counterparty) pair, and the two halves are
NOT equally applicable on a brokerage. The payee is the repeated thing
and applies whatever the shape; the counterparty often has nowhere to go.
So the halves are treated separately:

  - **The chip is offered on every action.** The payee is always applied.
  - **The counterparty is applied only into a category slot** — an action
    whose layout has one (`dividend_cash`, `dividend_reinvest`, `divx`,
    `misc`) — and only when it is not a Holdings sub-account. The fee slot
    is also a category counterparty, but which of the two a suggestion
    meant is not recoverable from the pair, so it lands on the category.
  - **The chip hides its "→ X" in exactly the cases it will not apply X**,
    so a chip never advertises something it then skips.

**Corrected 2026-09 (same day, from the dev rig).** Both of those rules
started stricter and were wrong for it. The chips were gated to actions
with a category slot, and the repository dropped any suggestion whose
counterparty was a Holdings sub-account — reasoning that "categorise it
as Holdings" is meaningless (ADR-0019: it is structural), which is true.

What that missed is that dropping the row discards its PAYEE, and on a
brokerage the row carrying the name the user curated is very often
exactly that buy: a settled buy has two legs, one on the brokerage, and
its other leg IS the Holdings sibling. On the dev rig a curated payee sat
on such a buy and recall stayed silent — twice over, since the row being
edited was a sell, which has no category slot and so showed no panel at
all.

The repository now returns the row and the caller decides which half it
can take. The rule of thumb this leaves: filter at the point of USE, not
at the source, when the thing being filtered is one half of a value.

## Consequences

  - Investment txn create / edit moves to a dedicated endpoint;
    bank `/transactions` no longer needs investment-shape
    validation.
  - The action × field matrix is the contract; adding a new
    action is an ADR-0027 + ADR-0029 update + a row in the
    matrix, not a branch in the API.
  - The shared `Coffer.Domain.Investment` namespace becomes the
    canonical location for posting-shape rules. The four queued
    feed-source slices (SimpleFIN / OFX / CSV / merge) all
    consume the same builders — no per-source re-implementation.
  - Manual security entry from the editor reuses the A3
    securities modal (no new "add security inline" UX).
  - Edit-Lots affordance (manual lot override) remains parked for
    A5 — the editor's FIFO consumption is automatic and cannot
    be user-overridden in this slice.
