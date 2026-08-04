# 0085 — Reporting values reality: `is_active` never gates a valuation aggregate

* Status: Accepted
* Date: 2026-07-25
* Related: ADR-0056 (Overview / net worth), ADR-0063 (`net_worth_history` / `returns` MCP reads), the inactive-account lifecycle (the sole intended use of `is_active`)

## Context

`accounts.is_active` exists for **one** purpose: the inactive-account lifecycle — hide closed accounts from pickers and the sidebar by default (a *presentation* concern). It leaked into reporting: the current net-worth (`OverviewRepository`) and `net_worth_history` (`AccountsReportingRepository`) queries filtered `WHERE is_active`, and `net_worth_history` compounded it by treating the *current* active set as static across the whole window.

Consequences:
- **Historical undercount.** An account open during period T but since closed (e.g. a 401(k) rolled over mid-window) was dropped from **every** historical point — the as-of feeder was never even asked to value it. Net-worth history understated reality for as long as the account had been closed (observed as a spurious ~$1.4M step when the closed account "disappeared").
- **Current undercount.** A closed account still holding value was excluded from *current* net worth too.

There is no valid reason to exclude a closed account from a valuation: a report reflects what value existed (then or now); whether an account is currently surfaced in the UI is irrelevant to what it is worth.

## Decision

**Valuation / aggregate reporting never gates on `is_active`.** It is a UI-surfacing flag, not a correctness filter.

- **Current net worth** (`OverviewRepository`) values every real (asset/liability-typed, non-holdings-sibling) account regardless of `is_active`; `GetCurrentBalancesAsync` is called with `activeOnly: false`. A closed-and-zeroed account contributes 0 anyway.
- **`net_worth_history`** values every real, non-category, non-sibling account at each point via the as-of feeder — no `is_active` filter, no static-current-membership assumption. The feeder already values each account by its state at T (nonzero while open, ~0 after liquidation).
- **`returns` / TWR and the investment tools** already do this (no `is_active` gate) — unchanged.

**Catalogs may still offer an opt-in `includeInactive`.** A *list* report (e.g. `list_accounts`) whose rows are self-contained (each carries its own balance + `is_active`) may hide closed accounts as a presentation convenience — there is no total to desync. `is_active` is surfaced as an **output field** for the consumer to filter on, never a query gate on an aggregate. `GetCurrentBalancesAsync`'s `activeOnly` parameter is documented as catalog-listing-only.

**Membership is not bounded by `created_at`.** An account "existed at T" cannot be derived from `accounts.created_at` — for an imported ledger that is the *import* timestamp, not the real open date, so a `created_at <= T` bound would wrongly drop every imported account from historical points. Membership is simply "every real account"; the transaction stream (as-of balance / holdings ≤ T) drives the value, which is 0 before the account had any activity.

## Consequences

- Net-worth history and current net worth include closed accounts at their real (as-of) value; the historical understatement is fixed.
- A known, smaller residual (separate): the as-of feeder's `COALESCE(latest.balance_after, opening_balance)` counts a non-zero `opening_balance` at points before the account's first transaction (undated opening balances can't be bounded cleanly). Usually nil (feed/import accounts open at 0). Tracked as a follow-up, not addressed here.
