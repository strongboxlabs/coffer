# 0056 — Ledger overview dashboard

* Status: Accepted
* Date: 2026-06-22
* Related: ADR-0021 (design system / tokens), ADR-0037 (snapshots),
  ADR-0049 (reminders), ADR-0050 (accounts management), ADR-0055 (provider-run
  audit), ADR-0057 (user-preferences store — slice 2), ADR-0020 (multi-ledger /
  RLS)

## Context

Opening a ledger lands on the **Ledger Hub** (`/ledgers/{id}`,
[`LedgerDetailPage`]) — a navigational index that lists accounts (grouped by
type), categories, and the top securities, plus header links to Reminders /
Bank feeds / Activity / Settings. Its own doc-comment is explicit: *"Sidebar
entries for the per-entity management pages are intentionally absent — the Hub
is the single discoverability surface."* There is **no sidebar**: the Hub is the
only path into registers, the securities catalog, and the management pages.

The Hub answers *"where do I go?"* but not *"how am I doing?"* — it shows no
balances, no net worth, nothing time-relevant (what's due, what just synced). A
user opening their ledger sees a list of names, not a financial picture.

We want a **dashboard / overview**: the financial summary you land on. Because
the Hub is the sole discoverability surface, the overview must **absorb** that
navigation role — it cannot be summary-only or it orphans every register.

What exists to build from:

- **Reusable read endpoints** — `GET /reminders/upcoming?from&to` (ADR-0049)
  and `GET /provider-runs?provider&days&limit` (ADR-0055), both per-ledger.
- **Per-account holdings** — `GET /accounts/{id}/holdings` returns a
  `PortfolioSummaryDto` (value / cost basis / unrealized) but only per
  investment account; there's no ledger-wide roll-up.
- **Gaps (no endpoint):** ledger **net worth**, **per-account balances** (a
  balance only exists as the register's running `balance_after`), and a
  **portfolio total** across investment accounts.
- **UI primitives:** `KpiTile` (label + mono value + colored delta, built for a
  stat strip) and `Panel`/`PanelHead`/`PanelBody`.
- **No user-prefs storage** — only localStorage (section collapse) and the
  server-side `last_opened_ledger`. Cross-device widget config needs a new
  table.

## Decisions

### D1 — The overview replaces the Hub at `/ledgers/{id}` and absorbs its navigation

The overview becomes what you land on when you open a ledger (the
`/ledgers/{id}` route; the old Hub layout is retired). Modern finance apps
(Monarch, YNAB) land on a summary, not a raw account tree.

Because the Hub was the single discoverability surface, the overview keeps every
entry point it provided:

- The **Accounts widget lists every account** (not just type subtotals), each
  row linking into its **register** — preserving the Hub's primary navigation
  while *adding* the balance it never showed. Group headers carry per-type
  subtotals.
- The header keeps the Reminders / Bank feeds / Activity / Settings links, plus
  entry points to the Securities catalog and Accounts-management page.

No sidebar is introduced (out of scope; the overview remains the single
surface).

### D2 — One server-computed `GET /api/ledgers/{id}/overview` endpoint

A single aggregate call returns the summary, server-side (layer independence —
we do not sum N register balances in the browser). Shape (v1):

- `netWorth`, `totalAssets`, `totalLiabilities` (assets − liabilities).
- `accounts[]` grouped by type: `{ id, name, type, balance, currencyCode }`
  plus per-type subtotals.
- `portfolio`: ledger-wide `{ value, costBasis, unrealizedGain, percentChange }`
  rolled up across investment accounts.

Balances reuse the **register's own balance source** — the trigger-maintained
`txn_header_account_balances` (the override-aware header-walk, ADR-0034), never a
parallel re-sum. Per-account balance = `opening_balance + Σ net_amount` (one
`GROUP BY` for the whole ledger; the cumulative `balance_after` equals that sum,
so no per-account "latest row" lookup). Investment accounts add holdings market
value (qty × latest `security_prices`, carrying no-price positions at cost
basis — matching the Portfolio View). **Liabilities are stored as negative
balances** (a credit-card/loan balance owed is negative), so **net worth is a
straight sum of every account balance** — not assets-minus-liabilities on
positive magnitudes. Repository is LINQ/EF (no raw SQL in the API).

The **Upcoming** and **Recent activity** widgets do **not** go through this
endpoint — they reuse `/reminders/upcoming` and `/provider-runs` directly.

### D3 — v1 widget set (fixed order)

1. **Net worth strip** — `KpiTile`s: Net worth · Assets · Liabilities ·
   Investments.
2. **Accounts** — every account grouped by type, with balance + per-type
   subtotal; each row → its register (D1).
3. **Investments** — ledger portfolio value + unrealized gain + top movers;
   links to the securities catalog.
4. **Upcoming** — next ~5 reminder occurrences (reuse).
5. **Recent activity** — last ~5 provider runs (reuse).

Layout: net-worth strip full-width on top; the rest a 2-column grid that stacks
to 1 column on narrow screens. Each widget is a `Panel` with its own
loading/empty state and a "view all →" link to its full surface.

Deferred (no data yet, not in scope): spending-by-category, budgets,
net-worth-over-time chart.

### D4 — Widget config is a later slice, not v1

v1 ships a **fixed** widget order. **Pick-and-choose** (show/hide + reorder) is
**slice 2**, and its persistence rides the **general per-ledger user-preferences
store (ADR-0057)** — the dashboard layout is one *namespace* among many
(appearance, register defaults, section-collapse, …), **not** a
dashboard-specific table. A full drag-resize grid is rejected — over-engineering
for this surface.

## TBD (not yet agreed)

- **Multi-currency net worth.** v1 assumes the ledger's base currency; summing
  across currencies (FX conversion) is a follow-up. The endpoint will surface
  per-account `currencyCode` so the UI can flag a mixed-currency ledger rather
  than silently mis-add.
- **Dashboard-layout preference shape** — the `dashboard` namespace of the
  general user-preferences store (ADR-0057); slice 2.
- **Net-worth-over-time** — depends on whether ADR-0037 snapshots can feed a
  trend; deferred.

## Slices

- **Slice 1** — the `overview` endpoint (net worth + balances + portfolio
  roll-up) and the Overview page with the fixed D3 widget set, replacing the Hub
  and absorbing its navigation (D1).
- **Slice 2** — pick-and-choose config (`user_dashboard_prefs` + show/hide +
  reorder UI).

## Consequences

- The Hub's account/category/securities index is superseded; the Overview must
  not regress discoverability (every register reachable in one click). Audited
  during slice 1 against the current Hub's links.
- A new ledger-wide balance aggregate is the first server-side net-worth
  computation — future surfaces (reports, snapshots) can reuse it.
- Net worth being server-computed means the SPA never re-derives money totals
  client-side, keeping a single source of truth.
