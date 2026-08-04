# 0073 — Investment money is authoritative at 2 decimals; price is derived

Status: Accepted
Date: 2026-07-10
Relates: [ADR-0027](0027-investment-action-catalog.md), [ADR-0029](0029-investment-transaction-editor.md), [ADR-0034](0034-header-walk-running-balance.md), [ADR-0064](0064-fifo-cost-basis.md)

## Context

An imported buy/sell carries three numbers from the wire: the **shares**, a
**per-share price**, and the **settled cash total**. Brokerages report the price
rounded (2–4 dp) but the total exactly, so `shares × price` does **not** equal the
total — e.g. `4.878 sh @ 29.45 = 143.6571`, but the real total is `143.68`.

The API accept/edit path (`InvestmentTransactionsRepository.BuildPostings`) built
the cash + holdings legs as `principal = price × |shares|`, **unrounded**, and
**ignored** the `Amount` field for share-trade actions. That one mechanism caused
three symptoms:

1. **The amount changed on Accept.** An imported row showing `143.68` (the wire
   total on the cash leg) became `143.66` once accepted (`29.45 × 4.878` rounded
   for display). The actual money paid was silently altered.
2. **Fractional amounts.** `143.6571` landed on the leg — `txn_legs.amount` is
   `numeric(19,4)`, so sub-cent money was storable. 56 such legs existed.
3. **Fractional / "-$0.00" balances.** Those sub-cent legs summed into the running
   balances (`txn_header_account_balances`), so a balance of `-0.0048` rendered as
   "-$0.00".

The editor's price↔amount link also guarded on `shares > 0`, so on a **sell**
(shares are negative) editing the amount never moved the price; since the amount
only reached the server *through* the derived price, Save looked like a no-op.

## Decisions

### D1 — Amount is authoritative money at 2 decimals; price is derived

For every action that carries an `Amount` (`buy` / `sell` / `buyx` / `sellx` /
`dividend_reinvest`, plus the already-direct `dividend_cash` / `transfer` /
`misc`), the request **`Amount` is the money** — the actual settled cash, exactly
2 decimals. The cash + holdings legs store it directly. The per-share
**`unit_price` is derived** = `amount ÷ |shares|`, rounded to **6 decimals** (the
register's max display precision via `formatPrice`), so what is stored equals what
is shown — never more digits in the DB than on screen. The wire's originally
reported price is preserved separately in `ingest_unit_price`.

`price × shares` need **not** equal the amount — a rounded price against an exact
total is normal and faithful to the feed (see
[[feedback_importers_report_feed_not_cash_model]]). Lot cost basis uses the same
authoritative amount, so basis == cash paid.

When a caller omits `Amount` on a share-trade action, the server falls back to
`round(price × |shares|, 2)` — a defensive path that still guarantees 2dp money.
Rounding is half-away-from-zero to match Postgres `round(numeric, n)`.
(`InvestmentTransactionsRepository.ResolveTradeMoney`.)

### D2 — Editor recalc matrix (edit screen only; the server never recomputes)

Three fields, one invariant (`amount = shares × price`); each edit holds one field
and derives a third:

| Edit   | Recomputed |
|--------|------------|
| Amount | price = amount ÷ shares |
| Shares | amount = shares × price (2dp) |
| Price  | shares = amount ÷ price (sign kept) — or, on a fresh row with no amount yet, bootstraps amount = shares × price so "N shares @ $P" manual entry works |

On opening an imported row for Accept, `hintToDraft` seeds `amount` = the real
total and `price` = `amount ÷ shares`. All arithmetic uses **magnitudes** — sells
store shares negative while amount/price are non-negative; the old `> 0` guard
silently disabled the link on every sell.

### D3 — Scrub residual state + guard (migration 159)

Round the 56 existing sub-cent leg amounts to 2dp (sec/income pairs are ±X and
`round()` is symmetric, so pairs stay balanced), re-derive their `unit_price` from
the rounded amount, rebuild running balances (`fn_recompute_balances_for_account`)
and holdings cost basis (`recompute_holdings_cost_basis`) for affected
accounts/ledgers, then add `CHECK (amount = round(amount, 2))` on `txn_legs` so
money can never again carry sub-cent fractions. For rows accepted *before* this
change the original import total is unrecoverable, so those round to 2dp; only
future accepts preserve the exact total.

## Consequences

- Accepting an imported trade preserves the exact amount; the displayed per-share
  price may gain decimals (e.g. `29.454695`) to reconcile — this is the true
  execution price and is stored as shown.
- The 2dp `CHECK` encodes the current **USD / 2-decimal** money model (all money
  is formatted at 2dp). Multi-currency with non-2dp minor units would revisit the
  constraint alongside the formatters — TBD, out of scope here.
