# 0018 — Cross-account-transfer and investment-transaction translation

* Status: Accepted
* Date: 2026-05-08
* Refines: [ADR-0016](0016-moneydance-account-translation.md)
* Refined by: [ADR-0019](0019-symmetric-postings.md) — supersedes the
  shadow-row + `splits` + `inv_txn_securities` model. Read this ADR for
  the per-shape decomposition; consult ADR-0019 for the row-pair model
  and the Holdings sibling account that hosts the holdings-side legs.
* Action-set update (migration 043): the per-leg directional pair
  `transfer_in` / `transfer_out` has been collapsed to a single
  `transfer` action — leg `amount` sign already encodes direction.
  `contribution`, `withdrawal`, and `fee` were dropped (zero rows in a
  large real-world MD export; superseded by `transfer` and `misc_expense`). Final
  9-action CHECK: `buy`, `sell`, `dividend_cash`, `dividend_reinvest`,
  `interest`, `transfer`, `misc_income`, `misc_expense`, `split`.

## Context

Phase 2 prep for the investment-transaction mapper surfaced two related
issues:

1. **Cross-account-transfer balance gap.** Moneydance encodes a transfer
   between two non-category accounts (e.g. checking → savings, brokerage →
   bank, "buy with cash transferred from another account") as a single
   txn with a split pointing at the other account. Our running-balance
   trigger sums `transactions.feed_amount` per `account_id`; splits
   contribute nothing to `balance_after`. The result: only the txn's
   primary account sees the transfer in its register; the other account
   is silently wrong. The non-investment transaction mapper (PR 2.5)
   shipped this gap unchanged.

2. **Investment-transaction shape variety.** The thousands of investment txns
   in a large real-world MD export decompose into nine distinct
   `(invest.txntype, xfer_type)` shapes, each with a different mix of
   `sec` / `inc` / `fee` / `xfr` splits. A naive "treat them like normal
   txns" approach loses the security side (which lives in `holdings` /
   `inv_txn_securities`, not `splits`) and breaks balances on transfer-
   shaped variants.

Both problems share a structural answer.

## Decision

### Rule 1 — Cross-account transfers emit two `transactions` rows

When a Moneydance txn references **two non-category accounts** (the
primary `acctid` plus any split whose target account is not a category),
the importer emits **two `transactions` rows**, one for each affected
account, **linked by sharing the same `external_id`** (the Moneydance
txn UUID).

The schema's existing partial unique index
`uq_idx_txn_external (account_id, external_id) WHERE external_id IS NOT NULL`
already permits this (uniqueness is per-account, not global). Each side's
`balance_after` is maintained correctly by the existing per-statement
running-balance trigger. Reports that need to recover "the whole transfer"
group on `external_id`.

This applies uniformly:
- Non-investment transfers (e.g., checking → savings) — back-filling the
  PR 2.5 gap.
- Investment-with-transfer txns (`buyx`, `sellx`, `divx`, `bank` from
  Moneydance) — natively fits the rule.

For txns with multiple non-category split targets (rare but possible:
3-way transfer), one `transactions` row per affected account.

### Rule 2 — Investment-txn translation is shape-driven

The investment mapper handles each `(invest.txntype, xfer_type)` shape
explicitly. The full table:

| MD shape | Relative frequency | Coffer `transactions` rows | Splits | `inv_txn_securities` | Holdings effect |
|---|---|---|---|---|---|
| `(buy,  xfrtp_buysell)` | common | 1 in brokerage, `feed_amount = Σpamt`, `'buy'` | 0 (or 1 to fee EC if a fee split exists) | `qty=samt[sec]`, `unit_price=\|pamt[sec]/samt[sec]\|`, `commission=\|Σpamt[fee]\|` | `qty +=`; `cost_basis +=` total cost incl. fee |
| `(sell, xfrtp_buysell)` | common | 1 in brokerage, `feed_amount = Σpamt`, `'sell'` | 0 (or 1 fee) | `qty=samt[sec]` (negative), `unit_price`, `commission` | `qty -=` (no lot-closing in Phase 2) |
| `(div,  xfrtp_dividend)` | common | 1 in brokerage, `feed_amount = pamt[inc]`, `'dividend_cash'` | 1 to income category | `qty=0`, `unit_price=0` (informational link) | unchanged |
| `(divr, xfrtp_dividend)` | common | 1 in brokerage, `feed_amount = 0`, `'dividend_reinvest'` | 1 to income category (records gross dividend) | `qty=samt[sec]` (new shares), `unit_price` | `qty +=`; `cost_basis +=` gross-dividend |
| `(buyx, xfrtp_buysellxfr)` | rare | **2 rows**: brokerage `feed_amount=0` `'buy'` + other-account `feed_amount=samt[xfr]` `'transfer'` | 0 on each | 1 attached to brokerage row | as `buy` |
| `(sellx, xfrtp_buysellxfr)` | rare | **2 rows**: brokerage `feed_amount=0` `'sell'` + other-account `feed_amount=samt[xfr]` `'transfer'` | 0 on each | 1 attached to brokerage row | as `sell` |
| `(divx, xfrtp_dividendxfr)` | rare | **2 rows**: brokerage `feed_amount=0` `'dividend_cash'` + other-account `feed_amount=samt[xfr]` `'transfer'` | 1 income split on brokerage | 1 attached to brokerage, `qty=0` | unchanged |
| `(bank, xfrtp_bank)` | occasional | **2 rows**: both `'transfer'`; brokerage `feed_amount=pamt[xfr]`, other-account opposite-sign | 0 on each | none | unchanged |
| `(inc,  xfrtp_miscincexp)` | rare | 1 in brokerage, `feed_amount=Σpamt`, `'misc_income'` | 1-2 (income + optional fee) | `qty=0` (informational link) | unchanged |

### Rule 3 — Fees recorded twice on purpose

> **Superseded by migration 046 (2026-05-18).** The original
> redundancy (split-to-fee-category + `inv_txn_securities.commission`
> column) was dropped because the column was never populated after
> ADR-0019's symmetric-postings rewrite turned the fee leg into a
> separate paired `txn_headers` row under one `txn_group_id`. Under
> the current model:
> - The **fee leg** (separate row, posting against the fee category)
>   is the single source of truth for the cash effect; user-facing
>   "fees this year" reports sum that leg.
> - **`lots.unit_cost`** carries the apportioned commission for
>   cost-basis math (computed as `price + apportioned_commission` at
>   import time in `InvestmentTransactionMapper`).
>
> The original text below is preserved for historical context — the
> per-shape import code paths still match it for the buy/sell shape,
> just without the (always-zero) `commission` write.

> **Amendment — 2026-05-19 (migration 054, slice B0.1).**
>
> Between migration 046 and migration 053, `lots.unit_cost` lost its
> commission-aware behavior — the computation that built it dropped
> commission when the per-leg column went away. The 053 lots rebuild
> used `txn_legs.unit_price` directly (per-share, no fee).
>
> Migration 054 restored the original intent with a **per-category
> opt-in flag** (`accounts.is_trade_commission`, default FALSE) plus
> an "Option B" structural gate (fee leg's cash counterpart must be
> on an investment account). Default behavior was identical to
> post-053.
>
> **Superseded by the migration 056 amendment below.** The
> per-category axis was wrong: category is malleable (a user can
> rename or reassign), but the intent of a posting (whether it's a
> trading fee) is not. 056 moves fee identification to the posting
> itself.

> **Amendment — 2026-05-19 (migration 056, slice B0.4).**
>
> Replaces the per-category flag model from 054. The principle: the
> category a posting is assigned to is metadata; the *intent* of the
> posting (whether it's a fee) lives on the posting itself,
> immutable to category changes.
>
> **Two independent flags:**
>
> 1. **`txn_legs.posting_role`** (new column, `TEXT` with CHECK in
>    `{security, income, transfer, fee}` ∪ NULL). The importer stamps
>    this from MD's `invest.splittype` for every investment leg; the
>    editor (future A4) sets it when adding postings. Both legs of a
>    posting share the same role. NULL on non-investment legs.
>
> 2. **`accounts.is_trade_commission`** (existing column from 054,
>    semantics narrowed). Now constrained by CHECK to investment
>    accounts only. On a brokerage: TRUE = `posting_role='fee'`
>    postings in that brokerage's transactions flow into cost basis;
>    FALSE = ignored. Defaults FALSE.
>
> Migration 055's UPDATE of "Fees" categories is reverted (categories
> can no longer carry the flag). Existing data is backfilled
> heuristically: legs in investment headers are tagged with the role
> they would have carried under explicit stamping (see migration 056
> for the precise heuristic).
>
> The recompute function becomes crisp — no category inference
> anywhere:
> ```
> fee_total = SUM(amount) WHERE same_header AND posting_role='fee' AND amount>0
> brokerage.is_trade_commission ? basis += leg.amount + fee_total : basis += leg.amount
> ```
>
> Category is irrelevant to basis. A user who marks a fee posting as
> a fee and assigns it to "Investment Fees" or "Random Misc Expense"
> or "Brokerage Commission" gets identical math — the posting role
> is the truth; category is just a free-form label for "fees this
> year" reports.

For `buy` / `sell` with a fee split, the importer writes:
- One **split** on the brokerage txn pointing at the fee expense category
  (so user-facing fee aggregation works: "how much did I pay in
  brokerage fees this year?").
- The same amount on `inv_txn_securities.commission` (so capital-gains
  cost-basis math is correct: cost-basis includes commission per IRS).

The redundancy is intentional — each field serves a distinct concern.

### Rule 4 — Lot-closing on sells is deferred

PR 2.6 records every `buy` and `divr` as a new `lots` row. Sells produce
`inv_txn_securities` rows with negative quantity but **do not close
lots** (no FIFO match, no `is_closed` update). Lot-closing is genuinely
complex (FIFO vs specific-identification vs LIFO; partial closes; tax
consequences) and gets its own phase with deliberate test coverage
against IRS scenarios. Until then, the `lots` table represents the open-
side of buys only; reports that need realized gains will compute against
the sells in `inv_txn_securities` using a separate strategy.

> **Amendment — 2026-05-19 (migration 053).**
>
> `holdings.cost_basis` was *previously* the un-decremented sum of every
> acquisition cost ever (Buy + DivReinvest), with Sells contributing
> `costBasisDelta = 0` per this rule. After many hundreds of Sell rows scrolled
> through one of the test datasets, that column read a large multiple of
> the true market value — the hero card surfaced as a large fake
> unrealized loss.
>
> Migration 053 introduces `recompute_holdings_cost_basis(ledger_id)`,
> a PL/pgSQL function that walks every holdings-side leg in
> `posted_at` order and applies the **average-cost method**: positive
> qty adds `leg.amount` to basis; negative qty reduces basis by
> `(running_basis / running_qty) × |sell_qty|`. The importer pipeline
> calls this function as Pass 5 of `InvestmentTransactionImportStep`,
> so `holdings.cost_basis` is now the cost basis of currently-held
> shares — not lifetime gross.

> **Amendment — 2026-05-19 (migration 054, slice B0.2).**
>
> The deferral above is **retired**. Migration 054 extends the same
> recompute function with **FIFO lot closure**: for each Sell event,
> open lots are drained in `acquired_at ASC` order, with partial
> closes decrementing `lots.quantity` and full closes flipping
> `lots.is_closed = TRUE`. Acquired quantity is preserved via the
> lot's `leg_id` pointer to the source `txn_legs` row, which is
> immutable.
>
> FIFO is the default closing strategy (matches most US brokerages'
> default for taxable accounts, and is correct for tax-deferred
> accounts where order doesn't affect liability). A5's Edit Lots
> affordance will let the user reassign closures for specific-id /
> tax-loss-harvesting cases without altering the FIFO baseline.
>
> The function is **idempotent**: it resets every lot's `quantity`
> and `is_closed` from the source `txn_leg` at the start of each
> holding's loop, then replays the event stream. Flipping any
> downstream config (commission flag, future closure-strategy
> override) and re-running converges to the same result regardless
> of prior lot state.
>
> Holdings' avg-cost `cost_basis` and lots' FIFO `is_closed` are
> orthogonal: avg-cost is a per-security rollup; FIFO is a
> per-lot attribution. A5 may surface a per-lot view that uses
> FIFO basis as its default, but the hero card stays on avg-cost.

> **Superseded — 2026-06-26 ([ADR-0064](0064-fifo-cost-basis.md), migration 148).**
> `holdings.cost_basis` is now **FIFO** (Σ open-lot cost), not average-cost — the
> two surfaces are unified, and each sale records a `realized_gains` row. The
> "hero card stays on avg-cost" note above no longer holds. ADR-0065 (mig 152)
> further amended the recompute for in-kind `transfer_shares` (lot-availability
> gate + zero realized gain).

### Rule 5 — Security side is `inv_txn_securities`, not a split

The `acctid` of a Moneydance `sec` split points at a `type='s'`
sub-account. ADR-0016 already established that those sub-accounts do not
become Coffer `accounts` rows — they translate to `holdings`. The mapper
therefore **never produces a Coffer split for a `sec` split**; it produces
`inv_txn_securities` (and, where applicable, `holdings` / `lots`).

Resolving "which Coffer security does this `sec` split refer to" is a
two-step lookup pre-built once during the import: walk every `acct` row
of `type='s'`, follow `currid` to the `curr` row, then through
`ImportContext.SecurityByMdId` to the Coffer `securities.id`. The result
is stashed on `ImportContext.SecurityIdByMdSecAcctId`.

## Consequences

**Positive**
- `balance_after` is correct on every account, for every transfer, in
  one consistent representation. No special-case "look at splits, too"
  trigger logic.
- The investment mapper has a single, table-driven structure. Each shape
  produces a deterministic set of rows; the test surface is per-shape.
- Reports composing "all rows for one MD txn" do `WHERE external_id =
  ...`; we pay no extra schema for the link.
- Lot-closing complexity is contained; PR 2.6 ships meaningful holdings
  data without locking us into a specific tax-lot strategy.

**Negative**
- A single MD transfer becomes two Coffer rows; queries that summarise
  "this transfer" must group/dedupe by `external_id`. Documented; the
  cost is bounded.
- For the rare 3-way+ transfer (none observed in a large real-world MD
  export, but possible in principle), the mapper emits N rows — one per
  affected non-category account. Sound, just unusual.
- `inv_txn_securities` carries `qty=0` rows for dividends and misc-income
  that don't change holdings. They serve as the link from a txn to the
  security it relates to and are useful for reports; we accept their
  presence.

## Alternatives considered

- **Extend the running-balance trigger to also sum splits where
  `splits.account_id = X`.** Considered for "single row per transfer,
  splits cover the other side". Rejected: the trigger logic doubles in
  complexity; reasoning about "what's my balance" requires understanding
  both rows-as-primary and rows-as-splits; the split table grows hot;
  every report has to know to look at both. The two-row representation
  matches users' mental model (each account has a register entry per
  event affecting it).
- **Add a dedicated `transfer_pair_id` column on `transactions`.**
  Considered as the cross-row link. Rejected: `external_id` already
  works, no schema delta needed, and `transfer_pair_id` would only ever
  carry the same value as `external_id` for these rows.
- **Decompose `divr` (reinvest) into separate "received dividend" + "buy
  shares" rows.** Conceptually clean but two-row representation for
  every reinvest (thousands of them) doubles the txn count. The single-row
  representation with `feed_amount=0` and a `dividend_reinvest`
  investment_action carries enough information for both income reports
  and holdings updates. Rejected.
- **Implement FIFO lot-closing now.** Deferred; the design space is
  large enough to deserve its own phase.
