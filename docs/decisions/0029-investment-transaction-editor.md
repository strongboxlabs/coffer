# 0029 — Investment transaction editor + endpoint surface

* Status: Accepted. **Extended by [ADR-0065](0065-transfer-shares-in-kind.md)** —
  the `transfer_shares` action adds a row to the action × field matrix (security +
  positive qty + destination investment account; no price/amount/fee).
  Amended 2026-11-14 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (a transfer account in another currency carries its
  own amount, a new row in the matrix and a new field in the request; the price input
  shows the brokerage's currency).
* Date: 2026-05-21 (amended 2026-11-04: the picker's words are ADR-0027's display words)
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
catalog actions: **Buy / BuyXfr / Sell / SellXfr / Dividend / Reinvest /
DividendXfr / Xfr / Misc** — plus **Transfer shares** (ADR-0065, in-kind move
to another investment account). Direction (income vs expense for Misc; in vs
out for Transfer) is **discriminated by amount sign**, not by
separate picker entries. Matches ADR-0027's data-model collapse.

Each entry's word is ADR-0027's display word, the one the register's action
chip shows: the picker (`ACTION_PICKER_ENTRIES`) holds the order and a hint
per action and takes every label from the register's one table, so the two
cannot name an action differently. *(Amended 2026-11-04: the dividend entries
read Dividend, Reinvest and DividendXfr, where they read Div, DivReinvest and
DivXfr.)*

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
layer (the no-raw-SQL rule for `src/Api`). FIFO consumption +
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

*Amended 2026-10-27 ([ADR-0108](0108-memo-and-tags-per-side.md) D1, D3, D4): memo and tags are the event's, placed
by the write primitive.*

- `memo` is decided by **presence** on the PATCH: omitted leaves it, `null` or
  blank clears it. `tags` keeps its meaning: omitted or `null` leaves them,
  `[]` clears them, a list replaces them.
- The writing side is the request's brokerage (an in-kind transfer's source
  brokerage). The value goes on that brokerage's leg when the event has one
  posting and on the header when it has several; its other rows show it
  inherited.
- An action change that changes the posting count moves the value between
  the brokerage leg and the header, and makes the brokerage the transaction's
  originating account.
- On an edit, an account already on the transaction is kept even if it has
  since been deactivated, and a newly added inactive one is refused; a create
  stays strict.
- A requested date on the stored calendar day keeps the stored posted
  timestamp, time of day included; only a different day changes it.

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

*Amended 2026-10-20 (FU-097):* the preview asks for the lots open on the
transaction's own date, without the transaction
(`?as_of=&exclude_header_id=`), from the same FIFO walk the writer uses.
Today's lots are the wrong set for a historical edit: on a since-closed
account there are none, so every historical edit would warn. The server refuses a save that would sell
more than was held, and the warning says so.

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

The merge slice parked above has shipped. In three places the investment
side carried a rule the bank side does not have, none of them a recorded
decision. All three are corrections toward bank, not new behaviour.

**1. Merge candidates constrain settledness only.** The bank rule is: any
accepted, un-merged, effectively-visible row on the same account, same
effective amount, within ±7 days. The investment query additionally
required a candidate to carry a SECURITY leg on the Holdings sibling,
with a non-null quantity and action — a SHAPE constraint on a rule about
settledness.

With that constraint a routine duplicate on a brokerage can never be
offered. A dividend the user already recorded as cash is
(brokerage cash, income category) with no holdings leg at all; a
feed-delivered `INVBANKTRAN` row is (brokerage cash, Uncategorized) with
no action either. The merge panel comes up empty on exactly the rows it
exists for.

The amount match now runs on the brokerage cash leg, which is bank's rule
verbatim. The share match on the Holdings sibling is kept ALONGSIDE it,
because it covers a case bank has no analogue for: a reinvest moves no
cash, so an amount match can never see one. A reinvest is also missed by
the cash-leg amount match when the edited row is a cash row of its
principal, since its income and purchase legs cancel on the brokerage; the
principal is on its holdings leg, so a third match reads the amount there,
in magnitude. A candidate's ticker and price
still come from its security leg when it has one, and are null on the chip
when it does not.

**The chip reads like the candidate's register row.** Its amount, share
count, derived action and other account are the values of the candidate's
row in the edited brokerage's register, read and projected by the register
itself (`RegisterRepository.GetRowsForHeadersAsync`), not a second reading of
the legs. The legs disagree with the row: a sell's holdings leg is negative
where its row is cash coming in, a principal leaves out a fee the row
includes, and an action-less cash transfer's `Xfr` exists only on the row.

The `ingest_security_id` narrowing still applies — but only to candidates
that HAVE a security to contradict. A cash-shape row carries none
*because* it has not been classified yet, which is precisely what makes it
the duplicate worth offering.

**Direction is part of the rule.** Bank compares the SIGNED sum on the
source account. A magnitude comparison (`Math.Abs`), on the security leg
or on the brokerage leg, offers a sell that brought in some amount the buy
that spent exactly that amount on the same security the same day: the same
magnitude, the opposite event, and a merge that would tombstone a real
transaction and halve the position.

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
check and the writes. Opening the transaction further down — one inside the
merge branch, one after validation — leaves the membership guard, the merge
branch's eligibility checks and the whole action × field matrix validation
unprotected.

This has no single-threaded symptom, so no ordinary integration test can see
it. It is pinned by a source-level assertion
(`WriteTransactionBoundaryTests`) rather than a concurrency harness: a
test that can only be written as a race is a test that gets skipped.

**3. Similar-payees recall reaches the investment editor.** The endpoint
needed nothing: a feed row lands BANK-shape whatever account it is bound
for — two legs, (real account, Uncategorized), with the investment detail
parked in the `ingest_*` carriers until someone classifies it — so its
anchor resolves on a brokerage exactly as on a checking account. Without
the call in the investment editor, a dividend categorised the same way
every quarter has to be categorised by hand every quarter.

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

**Why not stricter.** Gating the chips to actions with a category slot,
or dropping any suggestion whose counterparty is a Holdings sub-account
(true that "categorise it as Holdings" is meaningless, ADR-0019: it is
structural), discards the suggestion's PAYEE. On a brokerage the row
carrying the name the user curated can be exactly that buy: a settled buy
has two legs, one on the brokerage, and its other leg IS the Holdings
sibling. Recall would then be silent on such a buy — twice over when the
row being edited is a sell, which has no category slot and so would show
no panel at all.

The repository returns the row and the caller decides which half it
can take. The rule of thumb this leaves: filter at the point of USE, not
at the source, when the thing being filtered is one half of a value.

### Amendment (2026-11): the direction guard needs a direction

Merge candidates exclude a twin whose cash moved the other way, so a sell is
never offered as the twin of an equal-amount buy. That guard reads the edited
row's cash sign as its direction. For a row with a classification, an action or
an ingest action hint, it is one. For an unclassified feed row it is only the
feed's sign, and a feed that cannot tell a fund purchase from a sale reports
cash with its own convention: a buy can arrive as money in. The guard then hid
the row's true twin, and the panel was empty on the row it exists for.

The guard now applies only to a classified row. An unclassified row is offered
twins of both directions, the ones that agree with its sign first. Nothing is
decided for the person: the chip shows the twin's own direction and the merge is
their pick. A classified row (an imported sell, say) still never sees the buy
that closed the same lot.

The accepted cost: for an unclassified row, an equal-amount buy and sell on the
same day are both offered, so the person must read the chip's direction.

### Amendment, 2026-11-14 — a transfer account in another currency carries its own amount ([ADR-0114](0114-benchmarks-and-currency-conversion.md) D13, D14)

- **The matrix gains a row, the second amount (Received or Sent).** It is required for
  buyx, sellx, divx and xfr while the Transfer destination is a real account in another
  currency than the brokerage, and hidden otherwise and for every other action. It sits
  right of Amount, and for buyx, sellx and divx it pairs with the trade amount, not the
  fee. A new entry prefills it from Amount at the rate `RateDay.Of` gives its date,
  derived from the stored rates against the reporting currency (ADR-0114 D13, D14).
- **The request gains the transfer account's own amount**, a new field `transferAmount`
  beside `transferAccountId`, on the create and the PATCH body alike. It is required
  when `transferAccountId` is a real account in another currency than the brokerage, and
  dropped otherwise. A missing, zero or same-signed second amount is refused.
- **The price input's symbol is the brokerage's currency**, since price × shares =
  amount.

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
