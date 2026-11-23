# 0073 — Investment money is authoritative at 2 decimals; price is derived

Status: Accepted; D2 amended 2026-11-14 and D1, D2 and D3 amended 2026-11-16 by
[ADR-0114](0114-benchmarks-and-currency-conversion.md) (the second amount is outside the recalc matrix; the price input shows
the brokerage's currency; money is authoritative at its currency's ISO 4217 minor units,
not at 2 decimals, and the 2-place CHECK of migration 159 is replaced by a 3-place
ceiling)
Date: 2026-07-10
Relates: [ADR-0027](0027-investment-action-catalog.md), [ADR-0029](0029-investment-transaction-editor.md), [ADR-0034](0034-header-walk-running-balance.md), [ADR-0064](0064-fifo-cost-basis.md)

## Context

An imported buy/sell carries three numbers from the wire: the **shares**, a
**per-share price**, and the **settled cash total**. Brokerages report the price
rounded (2–4 dp) but the total exactly, so `shares × price` does **not** equal the
total — e.g. a three-decimal share count at a two-decimal price multiplies out to a
four-decimal product that sits a couple of cents away from the two-decimal total the
brokerage actually settled.

The API accept/edit path (`InvestmentTransactionsRepository.BuildPostings`) built
the cash + holdings legs as `principal = price × |shares|`, **unrounded**, and
**ignored** the `Amount` field for share-trade actions. That one mechanism caused
three symptoms:

1. **The amount changed on Accept.** An imported row showing the wire total on the
   cash leg became a slightly different figure once accepted (`price × shares`,
   rounded for display). The actual money paid was silently altered.
2. **Fractional amounts.** The unrounded four-decimal product landed on the leg —
   `txn_legs.amount` is `numeric(19,4)`, so sub-cent money was storable, and the
   product put it there.
3. **Fractional balances showing a negative zero.** Those sub-cent legs summed into
   the running balances (`txn_header_account_balances`), so a balance a fraction of
   a cent below zero rendered as a minus sign before a zero amount.

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

> **Note (added later).** The word "Amount" is also used by
> [ADR-0028](0028-investment-register-surface.md) for a DIFFERENT number — the
> register's per-account leg sum, i.e. the net cash through the sleeve. The two
> coincide on a plain Buy and diverge on every cash-neutral event. ADR-0028 now
> carries the reconciliation and the read surface calls this one `SettledAmount`.

`price × shares` need **not** equal the amount — a rounded price against an exact
total is normal and faithful to the feed (importers report the feed, not a cash
model; ADR-0042 D3). Lot cost basis uses the same
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

*(Amended 2026-11-14, [ADR-0114](0114-benchmarks-and-currency-conversion.md) D14.)* The second amount of a transfer between
currencies is outside this matrix. On a new entry, until the second amount is typed in,
it follows Amount (including an Amount derived from shares × price) and the date, at
the rate `RateDay.Of` gives that date, derived from the stored rates against the
reporting currency (ADR-0114 D13). Once typed, it stays, and a saved
transaction is never refilled. The price input's symbol is the brokerage's currency, since price × shares =
amount.

### D3 — Scrub residual state + guard (migration 159)

Round the existing sub-cent leg amounts to 2dp (sec/income pairs are ±X and
`round()` is symmetric, so pairs stay balanced), re-derive their `unit_price` from
the rounded amount, rebuild running balances (`fn_recompute_balances_for_account`)
and holdings cost basis (`recompute_holdings_cost_basis`) for affected
accounts/ledgers, then add `CHECK (amount = round(amount, 2))` on `txn_legs` so
money can never again carry sub-cent fractions. For rows accepted *before* this
change the original import total is unrecoverable, so those round to 2dp; only
future accepts preserve the exact total.

### Amendment, 2026-11-16 — money at each currency's minor units ([ADR-0114](0114-benchmarks-and-currency-conversion.md))

D1, D2 and D3 above stay as written: they record the 2-place model as it shipped, and
migration 159 is never edited. From [ADR-0114](0114-benchmarks-and-currency-conversion.md) D15 on, money is held at its own
currency's ISO 4217 minor units, 0, 2 or 3 places, not at 2 places for every currency.
Prices, share quantities and exchange rates are not money and do not follow this rule.

- **D1.** `Amount` is still the money and price is still derived, but the money is exact
  at the brokerage account's currency's places, not at 2 decimals. A typed amount finer
  than those places is refused with a 422 and a named error code that names the currency
  and its places (settled by Claude, 2026-11-16). The fallback for an omitted `Amount`
  rounds `price × |shares|` once at the account currency's places, half away from zero,
  as every figure the API computes is rounded (settled by Claude, 2026-11-16).
  `unit_price` is still derived to 6 decimals and gets no money rounding; a price shows
  at least its currency's places and at most max(6, places) (settled by Claude,
  2026-11-16).
- **D2.** The matrix's `amount = shares × price` rounds at the brokerage's currency's
  places, not at 2 decimals, and the Amount input's step and blur rounding follow the
  same places (ADR-0023 §H). The price input takes `step="any"` and gets no money rounding
  (settled by Claude, 2026-11-16).
- **D3.** Migration 301 replaces `ck_txn_legs_amount_scale_2` with a currency-agnostic
  ceiling, `CHECK (amount = round(amount, 3))`, added `NOT VALID` and then validated.
  Every row already stored is at 2 places, so every row passes. `txn_legs.amount` stays
  `NUMERIC(19,4)`. CLF and UYW, the fund units with four places, are refused as account
  and ledger currencies, so three places is the most any account's money needs. A CHECK
  on `txn_legs` cannot read the account's currency, so the exact per-currency rule is
  enforced in the API's writers, and the database keeps only the ceiling; there is no
  trigger ([ADR-0032](0032-triggers-as-last-resort.md)) (settled by Claude, 2026-11-16). The same migration
  redefines `recompute_holdings_cost_basis` to round at each account's places through an
  `IMMUTABLE` SQL minor-units function generated from the checked-in ISO 4217 data file
  the API and the SPA share (ADR-0114 D15), so lot basis still equals the cash paid
  (settled by Claude, 2026-11-16).
- **Figures already stored** that do not fit their currency's places are kept: there is
  no rounding migration and no pre-upgrade script. A write validates only the values it
  changes, so an edit that leaves such an amount unchanged passes. An account or ledger
  currency already stored that would now be refused for any reason (not an ISO 4217
  currency with an exponent, an "N.A." entry, or the 4-place fund units CLF and UYW)
  keeps working at 2 places, as today (settled by Claude, 2026-11-16). The consistency
  check lists the figures that do not fit and those codes as an advisory, as ADR-0084
  D6's unbacked-price advisory is: shown beside its verdict, not counted in it, and with
  no repair; the cure is an edit by an owner or editor (settled by Claude, 2026-11-16).

## Consequences

- Accepting an imported trade preserves the exact amount; the displayed per-share
  price may gain decimals (up to the six D1 keeps) to reconcile — this is the true
  execution price and is stored as shown.
- The 2dp `CHECK` encodes the current **USD / 2-decimal** money model (all money
  is formatted at 2dp). Multi-currency with non-2dp minor units would revisit the
  constraint alongside the formatters — TBD, out of scope here.
  *(Resolved 2026-11-16 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) D15: the constraint and the formatters were
  revisited together. The CHECK becomes the 3-place ceiling above, the exact rule per
  currency lives in the API, and money is formatted at its currency's minor units
  (ADR-0023 §H).)*
