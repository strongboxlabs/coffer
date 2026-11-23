# 0056 — Ledger overview dashboard

* Status: Accepted; amended 2026-10-31 by [ADR-0109](0109-overview-is-an-attention-first-home.md) (D1, D2 and D3: an attention-first home); amended 2026-11-13 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) (net worth converts into the reporting currency)
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

*Amended 2026-10-31 (ADR-0109).* The rail lists every account with its balance
(ADR-0106), so the rail carries navigation and discoverability. The Overview no
longer has to list every account, and its Accounts widget is hidden by default.

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

*Amended 2026-10-31 (ADR-0109 D8).* Every figure is still computed on the server,
but there is no single aggregate call: each widget uses its own endpoints, so it
fails and caches on its own and a hidden widget makes no request. `/overview`
remains the read for net worth, accounts and the portfolio.

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

*Amended 2026-10-31 (ADR-0109 D2, D3).* The net-worth strip and the grid are
replaced by a header and two independent column stacks, and the widget set by
ADR-0109's catalog, which includes this month's spending against budget and
net worth over time.

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
  than silently mis-add. *(Settled 2026-11-13 by [ADR-0114](0114-benchmarks-and-currency-conversion.md) D7: net worth
  converts into the ledger's reporting currency.)*
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

## Amendment, 2026-10-21 — a current balance stops where the register's Scheduled begins (mig 259)

**"Current balance" means everything the register does not call Scheduled, everywhere
it is shown.** D2 said balances reuse the register's own running balance, and the
implementation took the balance after the register's LAST row
(`account_current_balances`, mig 133), with holdings valued from the `holdings`
projection. Both include rows dated after today. So a scheduled payment, or a trade
entered before it settled, moved the sidebar, this dashboard, net worth and the MCP
balances today, while the register showed that row as Scheduled and the net-worth
history, which values each point as of its own date, left it out: two figures for
"now".

- **One boundary, the register's.** The register marks a row Scheduled when its
  `txn_headers.posted_at` (the date it shows, the effective date since mig 230) is at
  or after the caller's LOCAL tomorrow, expressed as midnight UTC, which is how the
  register stores a date (`RegisterFilter.Today`). Every current value counts exactly
  the rows before that instant: the same predicate, negated. One helper computes it,
  `ScheduledBoundary`, and the register's own status counts use it too, so the two
  cannot drift. It is not the server's `now()` and not the UTC date: for a caller west
  of UTC in the evening, a row dated on the UTC date is still Scheduled in the register
  and counts nowhere until the caller's date turns.
- **The caller's date.** The SPA sends its local date (`?today=YYYY-MM-DD`, from the
  same function the register's requests use) on every read of a current value: the
  sidebar balances, this dashboard, the investment register's portfolio card, the
  securities catalog and detail, and the consistency report (for the unbacked-price
  advisory's holding value). A caller that sends none (MCP, the quote refresh, the
  consistency monitor) gets the register's own fallback, the server clock's UTC date,
  from the same helper; nothing invents a rule of its own. The reminder reads and fires
  send no date: nothing on them is a current value (see the managed loan split below).
- **Cash and bank side:** the running balance after the latest row before the boundary
  (`account_balances_as_of_now`), opening-balance fallback. A later row is left out
  until the caller's date reaches it and then counts on its own: nothing is stored,
  refreshed or scheduled. The `account_current_balances` view keeps its shape and is
  that function at the register's SQL fallback, for any reader with no caller's date.
- **Investments:** cash as above, plus the positions held on that day
  (`holdings_as_of_now`) at the price a current value is computed at
  (`security_prices_as_of_now`): the latest close dated on or before the day, else the
  security's last trade price, back-adjusted onto the day's split basis for a split
  since that observation, by the rule the net-worth history's feeder applies. A
  position with neither is carried at cost basis (ADR-0084, 2026-10-21 amendment: one
  no-price rule for every valuation). A buy, sale, split or price the register calls
  Scheduled does not move today's value.
- **Where it applies:** the sidebar's accounts, folders and section totals
  (`GET …/accounts/balances`), this dashboard's accounts, net worth and portfolio
  roll-up, MCP `list_accounts`, `net_worth` and `account_portfolio`, the investment
  register's portfolio card, the securities catalog and a security's detail figures,
  and the "you hold" value on the unbacked-price advisory in ledger settings. MCP
  `holdings_snapshot` and `allocation` (and
  `GET …/reports/allocation`) take an instant; asked for none, they read the last
  instant the register does not call Scheduled (`ScheduledBoundary.LastCountedInstant`,
  at the register's fallback), not the clock's instant, so a row whose `posted_at`
  carries a later time of day today counts there as everywhere else. So does the quote
  refresh's working set ("held"
  securities, ADR-0054): a position sold by a future-dated sale is still held today,
  and keeps being priced until the sale's date.
- **It agrees with the net-worth history's last point** for every account and every
  position, priced or not: the cash side is the same running balance at the same
  instant, and the holdings side the same price tiers (close, last trade, cost).
- **What does not change:** the register's rows (a Scheduled row is still listed, with
  the running balance it will leave), the stored running balances, the `holdings`
  projection, the consistency checks that compare against them (they are unbounded
  like the writers, and must stay so), and anything on screen besides the numbers.
- **The managed loan split is not a current value.** It is priced on the loan's
  balance as of the payment's own date, never today in any time zone (ADR-0078,
  2026-10-21 amendment): a historical read at the payment's posted_at, not this
  boundary. When the auto-post job runs, or who fires the occurrence, does not move it;
  the job's ledger-local run date decides only which occurrences are due (ADR-0097).

Pinned by `CurrentBalanceAsOfNowTests`: a row on the boundary day, for a caller west of
UTC, counts on the sidebar, this dashboard and the portfolio card exactly when the
register's status counts, asked with the same date, stop calling it Scheduled; MCP,
with no date, follows the register's fallback on an injected clock; every surface
leaves a future-dated row out, counts a past-dated one, and agrees with the others, with
MCP `holdings_snapshot` and with the net-worth history's last point, including across a
split newer than the latest price and for an unpriced position (at its last trade
price, or at cost when it never traded at one); the register still lists the future
row with its balance; a row stamped later today counts in `holdings_snapshot` and
`allocation` as it does in `account_portfolio`. `RemindersLoanTests` pins the loan
split's own rule (ADR-0078).
