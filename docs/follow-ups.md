# Follow-ups

The whole not-yet-shipped backlog in one place: an ordered **Next** zone (what's
shipping soon, in ship order) followed by the unordered **Backlog** behind it.
(Merged from the former `roadmap.md`, 2026-07-24 — one file, one home per item.)

**Lifecycle.** Add an item when it's surfaced. **Shipped items are deleted, not
strikethrough-annotated** — this stays a list of open work, not an audit log (git
history is the audit log). When you commit to shipping a backlog item next,
**move** it up into Next — don't copy; an item lives in exactly one zone.
Bigger-picture phase status lives in [README.md](../README.md)'s table.

**Zones.**
- **Next (ordered)** — PR-sized slices intended to ship, in ship order. Each has a
  concrete shape (problem · approach · gating). Delete when it merges.
- **Backlog (unordered)** — everything below the Next section; shaped or not, no
  schedule. Grouped by area, not prioritised.

**Status legend.** Each backlog item has a short status line:
- *open* — surfaced, no schedule yet.
- *blocked on X* — waiting on a specific dependency.
- *partial — N of M done* — partially shipped; the remaining work is captured.

---

## Agreed roadmap (2026-07-28, reordered 2026-08-03, in order)

*Empty — every agreed item is delivered or assessed-and-closed. The **Next** zone
below is the queue; promote from Backlog when the next roadmap is agreed.*

Delivered and removed from this list (git history is the audit log): snapshot
restore performance (mig 188 + the on-demand scale lane; parked with ~9× timeout
headroom — see that section), CI
build-once + runner Docker reap (`scripts/ci-dotnet-shards.sh` + the ci.yml reap
step; the measured pre-migrated-image dead end is recorded in
[engineering-standards.md §9.1](engineering-standards.md#91-sharding-build-once-and-dont-assume-a-matrix-helps)),
the representative reference ledger (#414), and the observability + audit-trail
gap sweep (only the prod OTLP exporter remains — see Observability below).

**Closed, not deferred — the whole former roadmap #1.** Both halves were assessed
and closed; do not re-add either. The `holdings_market_value_as_of` unconstrained
return columns are a **known, accepted** blind spot: the magnitudes needed to
overflow are not reachable by any plausible portfolio on a ~50-year horizon (kept
documented under Systemic boundary testing so it reads as a deliberate acceptance,
not an oversight).

The phantom `$1`-placeholder in-kind transfer corruption exists only in one owner's
**legacy MD import**, which is import-once: no future import re-triggers it. PR #412 shipped
a generic importer step that reclassified every such transfer to a non-realizing
`transfer_shares`; PR #413 reverted it
because the population is heterogeneous and the blanket rule was actively wrong —
**orphan `sellx` rows are real sales** whose proceeds field was corrupted to the
share count, so making them non-realizing *erases real gains*. The rest are true
paired transfers, money-market $1-NAV, and genuinely cheap securities (a $0.55
option a loose price filter caught). It needs per-transfer judgment, not product
code, and was remediated by a one-off personal data scrub deliberately kept out of
the codebase. A future MD re-import needing correct `$1`-transfer handling is a
fresh design, not this item.

## Next (ordered)

### CSV Phase 5 — generic ingest provider (ADR-0031)

`GenericCsvProvider` reading a `feed_csv_mappings` config (column map + date
format + sign convention + header rows), extending the same `IngestOrchestrator`
as SimpleFIN / OFX / QIF. The column-mapping wizard UI lands here. Resolves the
CSV slice (file upload, per-institution mappings, hash-based `external_id`).

### CSV Phase 6 — per-institution ingest providers (ADR-0031)

Hand-coded providers for institutions whose format defeats the generic path (a
workplace 401(k) plan expected first). Each is a sibling `IFileProvider`
registered alongside the generic one; the orchestrator dispatches purely on the
provider key.

---

## Backlog

*Unordered, grouped by area. Promote an item into **Next** above when you commit
to shipping it.*

## Secrets handling

### Database credentials still travel by environment variable
*open (surfaced 2026-08-06, alongside ADR-0092).*

ADR-0092 moved the master KEK out of `COFFER_MASTER_KEK_BASE64` into a file,
because an environment variable is readable via `docker inspect`,
`/proc/<pid>/environ`, child process environments and crash dumps. The database
credentials still travel exactly that way, so the reasoning now applies unevenly.

The one that actually matters is **the API's connection strings**.
`docker-compose.yml` interpolates `COFFER_APP_PASSWORD` / `COFFER_SERVICE_PASSWORD`
into `COFFER_API__ConnectionString` and `…ServiceConnectionString`, which sit in the
API container's environment — so `docker inspect coffer-api` prints the credentials
the app authenticates with. Fixing it needs a path-valued option mirroring
`Api:MasterKey:Path` (say `Api:ConnectionStringPath`), or mounted Docker secrets
plus a config provider that reads them; .NET config has no `_FILE` convention to
lean on.

`POSTGRES_PASSWORD` is a smaller win and a one-liner: the official Postgres image
honours `POSTGRES_PASSWORD_FILE`. Note it's the *superuser* password, which the app
never uses — it connects as `coffer_app` / `coffer_service` — so it's second in
priority, not first.

`POSTGRES_USER`, `POSTGRES_DB` and `POSTGRES_PORT` are not secrets and should stay
where they are.

Worth being honest about the ceiling: anyone who can read `/proc` or run
`docker inspect` on the host can also read the Postgres data directory. The KEK
earned a file for two reasons these don't share — it must be *writable* (rotation
and adoption mutate it) and it is deliberately the one secret kept out of the
database so it can't ride along in a dump. This is real hardening, not a live
vulnerability, and it wants its own ADR rather than being bolted on.

### The backup passphrase ceremony is lighter than the master key's
*open (surfaced 2026-08-06).*

ADR-0092 D5b made the stored backup passphrase revealable behind a fresh assertion,
which fixed the silent failure (a forgotten passphrase meant every backup was
unrestorable with nothing saying so). What it did *not* do is give the passphrase
the save-it-now treatment the master key gets at first run: the master key is shown
during setup behind an acknowledgement, while the passphrase is only ever typed by
the operator into a dialog. Since the passphrase is what actually gates restoring an
artifact — a `.cofferbak` is sealed under it, not under the KEK — the argument for a
first-class "save this" moment is arguably stronger there. Needs a shape: probably a
prompt when backups are first enabled rather than another setup step.

## Localisation

### Per-user language/culture + a ledger main currency

Setup should eventually offer language/culture and a main currency. Two items of
very different weight, deliberately listed apart — do not ship them as one
"locale settings" ticket.

**Culture/language (small).** No `locale`/`culture` column exists anywhere today.
Add it to `users`, default from the browser's `Accept-Language`, and keep it
editable in settings — setup is the worst moment to force a permanent choice, as
the user has no data and no context yet. Drives UI strings and date/number
formatting only.

> **Invariant: a user's culture must never affect identity comparison.**
> Username folding, uniqueness and login lookup stay culture-independent
> (ICU `und-u-ks-level2` — "und" is *undetermined locale*, which is the point).
> If per-user culture drove folding, the same username string would resolve
> differently depending on who was logging in — the Turkish dotless-ı bug
> reintroduced with extra steps. Demonstrable on any install:
> `SELECT lower('İSTANBUL'), lower('İSTANBUL' COLLATE "C");` returns
> `istanbul` and `İstanbul`. See ADR-0089 (username identity).

**Main currency (large — not a dropdown).** Currency is data, not presentation.
Today `currency_code` lives on `accounts` and `security_prices`; there is
**no** ledger-level currency. Adding `ledgers.currency_code` as a *reporting*
currency forces a decision on what happens when an account's currency differs
from the ledger's, which pulls in FX rates, historical rates for point-in-time
valuation, and every existing report/aggregate. Scope it as its own ADR before
any UI is drawn.

## SPA / register

### Tax / transaction date — systemic surface

*Status: partial — `transacted_at` write plumbing + the read-only
bank `tax {date}` sub-label shipped; remaining: (a) a Tax-date
field in the editors, (b) Reports tax-year grouping opt-in on
`transactedAt`, (c) CSV export of tax date, (d) investment-register
treatment.*

The data column `txn_headers.transacted_at` is populated end-to-end
and the bank register renders a `tax {date}` sub-label under the
posted date when `transactedAt !== postedAt`. The remaining UX loop:

- **Editor:** no field in `TxnRowEdit` / `TxnRowCreate` for tax
  date. A user who needs to backdate a Dec-29-booked-but-Jan-2-
  posted dividend has no path. One PR adds a "Tax date" field
  (date-picker, defaulting to posted).
- **Reports:** the Reports module always keys off `postedAt`.
  Add an opt-in to use `transactedAt` for tax-year grouping.
- **CSV export:** expose the tax date.
- **Investment register:** the check_number sub-label (slot 3
  line 2) takes the spot that on bank shows tax date, so tax date
  on investment rows is currently invisible — needs its own
  treatment.

## Bank feeds

### Bulk security-mapping step in the OFX import dialog

*Status: open. Captured 2026-06-08 while testing PR #160.*

Today's investment-OFX flow records `(provider_key, ticker_hint) →
security_id` mappings one-at-a-time, lazily, when the user opens
each `needs_review` row in the editor (Phase 3d). After the first
resolution per security, the rest auto-link. That works but puts
the security-mapping work *inside* the per-row review.

For an OFX with N transactions across K distinct securities, the
user has to open K rows to clear the security-resolution work
before the remaining N − K rows auto-resolve. With current UX, the
user doesn't even know which K rows hold "new" securities until
they open them.

**Proposed:** add a "Match securities" step in the OFX import
dialog, parallel to the existing "pick the account" step. The
preview already has SECLIST data; show each discovered security
with its ticker / name / CUSIP and let the user:

- pick an existing Coffer security (typeahead like the editor's
  security picker), OR
- click "Create new" (reuses `AddSecurityDialog`), OR
- skip (mapping stays open; row falls back to per-row resolution
  in the editor).

On Import, record the chosen mappings in
`provider_security_mappings` before the orchestrator runs. Per
ADR-0038 the resolved view derives `ingest_security_id` from
this table on every read, so every row of the matched ticker
auto-resolves on the next register fetch. K decisions in one
batch instead of K trips through the editor.

## Register surface

### Register non-date scroll affordance

*Status: open. Surfaced 2026-07-14 during the column-sort dev review.*

Column sort shipped (mig 166, `feat/register-sort`): Date / Amount / Payee /
Category on every register, plus Security / Shares / Price / Action on
investment registers, via a sort-parameterized `register_entry_keys` keyset
cursor (filtering + search + status views + the security dimension shipped
earlier under migrations 164/165).

One rough edge deferred: sorting by a non-date column (or date-ascending) falls
back to the native browser scrollbar, whose thumb reflects only the loaded
~1000-row window — so it resizes / repositions as the windowed register pages in
and evicts. The date-rail (`RegisterScrollTrack`) avoids this for the default
date-desc view by replacing the scrollbar with a stable month/year index, but
non-date orders have no equivalent natural index. A stable affordance would
either feed the virtual list the true total entry count (already available from
`status-counts`) so the thumb is honest regardless of sort, or render a custom
total-count-based thumb in the rail gutter. Windowing-level work; deferred as
disproportionate to the sort slice.

### Splits editor — optimization / refactor

*Status: open. Requested 2026-07-11; shape TBD.*

The multi-split transaction editor wants a perf + code-structure
pass. Scope to be defined against the current editor.

### Bulk Categorize / Tag actions

*Status: blocked on the bulk override / categorisation write endpoints.*

The bulk-action footer in `RegisterPage.tsx` reserves `Categorize…`
and `Tag…` buttons (rendered disabled today). Wiring waits on the
bulk override / categorisation endpoints — neither exists yet.
Once those API surfaces land, the buttons gain handlers that
PATCH each selected header in the same optimistic-cache pattern
the recon-status bulk buttons use today.

---

## Sidebar

### Folder accounts (sub-grouping within types)

*Status: open. Schema change required.*

Users want a second level of organization inside each type —
"Checking" / "Savings" folders inside Banking, "Roth IRA" /
"Traditional 401(k)" / "Taxable" inside Investments. Folders are
pure grouping containers, NOT real accounts:
- no transactions of their own
- no `currency_code` semantics
- no clickable register
- balance computed as `SUM(child.balance)` at render time

A folder is a **distinct schema concept** from a parent account
that happens to have children. `accounts.parent_id` alone can't
tell the SPA "render as a folder, suppress the register link, roll
up the balance."

**Schema options** (surface options before coding):

1. `accounts.is_folder BOOLEAN NOT NULL DEFAULT FALSE` + a trigger
   forbidding `txn_legs` rows where `is_folder = TRUE`.
   Minimum-disruption, reuses the accounts table.
2. New `account_type = 'folder'` discriminator alongside `bank`,
   `category`, etc. Cleaner type-wise but touches every
   account-type switch (sidebar, register, importer).
3. Separate `account_groups` table — folders distinct from
   accounts. Most "correct" but biggest schema ripple.

Lean toward (1) — minimum surface area, single field every
account-aware query already projects.

### Sidebar tab reorder (drag-to-rearrange)

*Status: open. Schema column already reserved.*

Migration 033 ships `user_account_groups.sort_order INTEGER` so
the SPA can render tabs in a stable user-curated order; v1 just
appends new tabs to the end (max+1). Drag-to-reorder needs:

- A PATCH-level "reorder" surface — either extending
  `PatchAccountGroupRequest` with `sort_order` (per-tab) or a
  bulk `PUT /api/ledgers/{ledgerId}/account-groups/order` taking
  the desired id sequence. The bulk path is cleaner for a real
  drag-and-drop (one round trip instead of N).
- HTML5-drag affordance on the tab strip in `AuthedSidebar.tsx`
  (same pattern as `TxnRowEdit`'s posting reorder).

Land when the user actually wants to reorder — at 2-4 tabs the
append-order is usually fine.

### Remember last-active sidebar tab across sessions

*Status: open. Defer per design discussion.*

`AuthedSidebar`'s `activeGroupId` resets to "All" on every
refresh. The user explicitly chose "All by default, no
localStorage drift" for v1; the deferred path is a
`user_preferences` table (or JSON column on `users`) that holds
per-(user, ledger) UI state including last-active tab,
collapsed-section state, etc. Land when there's more than one
preference worth persisting.

### Drop the vestigial counters on `sync_runs`

*Status: open cleanup. Slice 2c.1 left `txns_merged` / `txns_queued` /
`txns_skipped` in place on `sync_runs` (always 0 — they're
from the pre-2c merge / staging pipeline). Migration 044
dropped the sibling vestigial tables (`pending_transactions`,
`merge_candidates`, `merge_rules`, `transaction_rules`); the
sync_runs counters survived because they live on a table we
still use. Drop them in a future cleanup.*

### Sync activity log retention

*Status: open. Slice 2c.1 keeps every `sync_runs` row forever. At
one sync/day that's ~365 rows/year/connection — fine through
the personal-use horizon. When daily polling lands (background
worker, not yet built) or a multi-tenant deployment shows up,
re-evaluate. Likely shape: keep last 90 days verbose, roll
older runs into a per-month summary row; or simply LIMIT the
list query and never paginate beyond N. No automation today.*

### Scheduled-sync worker (slice 2d-ish)

*Status: open. The scheduler framework exists (`scheduled_jobs` mig 136 +
`IScheduledJobHandler` / `IGlobalScheduledJobHandler`, already driving daily
backups + quote refresh); feed sync is manual-trigger only — no feed-sync job
handler is wired. This item is adding one.*

Today every `sync_runs` row carries a `triggered_by_user_id`; a background
daily-poll handler would land rows with NULL there. The activity panel + counters
already accommodate this — no UI change needed when the worker ships.

### Persist failed SimpleFinException runs more granularly

*Status: open. Slice 2c.1 captures the exception message verbatim
in `sync_runs.error_message`. Future polish: capture the
upstream HTTP status code + a redacted response body for the
diagnostics-fast-path case where the bank breaks the v2
contract. Probably as a JSONB column or a child table parallel
to `sync_run_errors`.*

### Bulk Approve via ADR-0024 selection

*Status: slice 2d. Right-click → Approve handles single rows
today; the ADR-0024 bulk selection machinery already exists
and would extend cleanly to a bulk-approve endpoint
(`POST /api/ledgers/{id}/transactions/bulk-approve` with a
`SelectionRequest` body).*

### Rule-based auto-categorization on sync

*Status: slice 2d. MD's screenshot shows the yellow
pending rows already carry categories (Insurance:Automobile,
Hobbies-Leisure:Entertaining, etc.). That comes from MD's
rules + payee memory. Today every sync row in Coffer lands on
the per-ledger "Uncategorized" counterparty. Build a small
rules engine (payee-substring → category, with priority +
on/off toggle) and apply it in `SimpleFinSyncService` before
the leg insert. Approve flow stays unchanged.*

---

## Investment data

### Per-security filter on the register Toolbar

*Status: open. Small SPA-only slice once A1.c (investment register
row rendering) lands.*

When viewing a brokerage register, the user often wants to see
just one security's history — MD's "Securities Detail" register
sub-view. Rather than a separate page, surface this as another
**Filter** dimension on the existing Toolbar (alongside `All` /
`Cleared` / `Uncleared` / `Scheduled`):

  Security: All ▾   →   IDXA | MMFA | ... (the account's currently-held tickers)

Predicate is server-side via a `security_id=<uuid>` query param
on the register page endpoint; LINQ `Where` against
`resolved_transactions.security_id`. Choices populated from
`HoldingsRepository.GetByBrokerageAsync` (already cached for
the Portfolio View) so no extra round-trip.

### A5 — Edit Lots affordance

*Status: queued after A4 (the editor + FIFO lot closure) lands.*

Post-A4 cleanup workflow. Per-security drill-in on Securities
Detail gets an "Edit Lots" button that lets the user reassign
which lots a sell consumed — the tax-loss-harvesting move that
FIFO doesn't cover automatically. Reads `lots.is_closed` /
`lots.quantity` and writes through a new endpoint that
re-balances the lot consumption against the sell leg without
touching `holdings.quantity` (totals stay correct; only the
attribution changes).

**Where:** new endpoint under
`/api/ledgers/{id}/securities/{sid}/lots`; UI on Securities
Detail.

**Cost-basis note (updated for ADR-0064 FIFO).**
- `holdings.cost_basis` is **FIFO** — Σ open-lot cost
  (`recompute_holdings_cost_basis`, ADR-0064 / migration 148; was average-cost
  under migration 053). Commission inclusion is gated by
  `txn_legs.posting_role='fee'` + the brokerage's `is_trade_commission`.
- Lots are **FIFO-closed on disposals** — acquired qty preserved via the lot's
  `leg_id` → immutable `txn_legs.quantity`; migration 152 (ADR-0065) added a
  lot-availability gate so in-kind transfer-in lots aren't consumed before they
  arrive.

A5's job is the **manual-override layer**: let the user reassign which lot a
particular Sell consumed (tax-loss harvesting — when FIFO would close a
high-basis lot the user would rather hold). The default FIFO consumption is
computed; A5 stores the override and the recompute honors it on the next pass.
`holdings.cost_basis` stays FIFO (Σ open-lot cost) — A5 changes *which* lots are
consumed, not the basis method.

### Stock-split lot fan-out

*Status: open. Triggers when A4 ships the `split` action button.*

A4's editor will offer `split` as one of the 8 actions, but the
*real work* of a corporate split is splitting every open lot
(e.g. 2-for-1: each lot's `quantity` doubles, `unit_cost` halves;
`acquired_at` stays so short-vs-long-term holding period is
preserved). This is non-trivial enough to deserve its own slice
once the editor is in place.

---

## Multi-user collaboration

### SSE notifications for live edits across users on the same ledger

*Status: open. Post-multi-user (concurrent-editing) shape.*

Setup ceremony now grants ledger membership, so a single ledger
can have multiple users. Polling is fine for low-conflict cases
but two people reconciling the same account simultaneously gets
surprising under last-write-wins.

ADR-0012 commits us to **SSE over plain HTTP** (no SignalR).
Shape:

- `GET /api/ledgers/{ledgerId}/events` returns `text/event-stream`;
  per-connection auth via the existing cookie; per-ledger RLS
  filter so events flow only for ledgers the caller can access.
- Mutation endpoints publish to a Postgres `NOTIFY` channel keyed
  by ledger id; a hosted service tails `LISTEN` and fans out to
  open SSE streams.
- SPA subscribes on active ledger; `txn-*` events call
  `invalidateLedgerRegister` on the ADR-0079 canonical `['register', …]` key so
  a mounted register reloads its rows (plus accounts / holdings). NOTE: the
  register's rows are a bespoke window, not a TanStack query — "TanStack handles
  refetch" only works because ADR-0079 makes the controller honor that key; a
  bare `invalidateQueries` was a silent no-op for the rows before it.

**Where:** new `Coffer.Api.Notifications` namespace; a
`PgNotifyListenerService : BackgroundService`; SSE handler in
the existing endpoints folder; web side gets a
`useLedgerEvents(ledgerId)` hook beside the query layer.

Defer until concurrent multi-user editing is real — the single-user happy path is
still the default, and SSE changes the API's hosting story (long-lived
connections, idle-timeout config, reverse-proxy buffering).

---

## Observability

### Prod OTLP tracing exporter

*Status: blocked on a collector existing to receive spans. The last open item from
[ADR-0086](decisions/0086-mcp-write-observability.md); the codebase-wide
observability + audit-trail sweep is otherwise complete.*

Opt-in via `OTEL_EXPORTER_OTLP_ENDPOINT` — spans are already produced, there is
just nowhere to send them. Everything else the sweep set out to do has shipped
across three batches: application-log silent-failure fixes plus a `/health`
write-gating fix; `is_error` retired (migration 184) so the admin viewer reads
`status` directly and `pending`/`cancelled` render, plus per-endpoint
business-outcome logging (`BusinessError.Problem` tags `HttpContext.Items` and the
access log appends the business `code` on a rejection); and durable audit rows for
Moneydance import + snapshot restore (`provider_runs` generalized to
`ledger_operations`, migration 185, surfaced in Settings→Activity).

## MCP + reporting

### MCP write/OAuth control-plane (PR C)

*Status: open. The last of the 2026-07-17 MCP-hardening tracks; PR A (read
surface) + PR B (investment aggregation) shipped.*

- Enforce `coffer.read` scope → genuine read-only tokens (today decorative; any
  token can call every write tool once writes are on).
- OAuth/DCR client list + revoke + prune UI (today only manual tokens are
  manageable; a claude.ai OAuth client can't be seen/revoked without DB access).
- Rate-limit anonymous DCR (`/oauth/register`; bounded only by the 50-cap).

(The immediate kill-switch shipped in 0.30.1 — it is now the sole write gate.
The per-call write audit lives under "MCP per-tool-call audit" below.)

### Budgets + budget-vs-actual

*Status: open. The next backend/product slice after historical valuations (PR B, shipped).*

Whole subsystem: schema (amount/category/period) + API + UI + variance report +
MCP exposure.

### Realistic anonymized demo import

*Status: open. Surfaced during PR B (historical valuations) design. User-requested; PII-safe approach agreed.*

The current `data/samples/moneydance-export-demo.json` is all-uncleared +
investment-only, which masked the reconciliation + valuation cases (it misled the
ADR-0082 recon work). Build a realistic demo by **synthesize-on-structure**: read
the real export ONLY for structure (account-graph topology, txn shapes,
stat/splittype distributions) and emit synthetic names/payees/tickers/account
numbers + jittered amounts + shifted dates — nothing real copied, so there is no
PII to leak. Deterministic C# generator (project stack, no Python); decide
replace-vs-new + update `DemoSampleImportTests` + provisioning.

### Canned + memorized reports (reuse the MCP reporting layer)

The MCP server (ADR-0063) introduces a reusable reporting layer:
a serializable **`ReportSpec`** (measure · group-by dims · filters ·
period · top-N · detail) + a **`ReportingRepository`** that aggregates
over the override-aware `resolved_transactions` view, plus the
investment read tools. **A future in-app Reports feature must sit on
this same layer, not a parallel one:**

- **Canned reports** (Moneydance parity target — Expenses, Income,
  Income & Expenses (+Detailed), Budget, Cash Flow, Net Worth, Account
  Balances, Tag Summary, Transfers, Portfolio, Asset Allocation, Cost
  Basis, Capital Gains, Investment Performance, Transactions /
  Transaction Filter, Reconciliation, Missing Checks, …) collapse to
  ~4 reusable primitives: transaction aggregation (category/tag/payee ×
  time), balances-over-time, investment roll-ups, and transaction
  query/filter. Build the MCP layer so each canned report is a preset
  `ReportSpec` rendered by a SPA Reports page — not new query code.
- **"Memorized" reports** = a persisted `ReportSpec` (same pattern as
  saved views / `user_preferences`), so users save + re-run report
  configs. The MCP tool params and the saved-report model share the
  one spec shape.
- MD's report **settings dialog** (date range, source-account select,
  tag filter, include-transfers, tax-related, include liability/loan,
  income/expense category tree) is the filter surface the `ReportSpec`
  should anticipate — v1 MCP exposes a subset; the spec is shaped for
  all of it so canned reports need no parallel model.
- v2 returns (IRR/TWR) feeds Investment Performance; v3 FX unblocks
  multi-currency reports. Likely its own ADR when the Reports UI is
  scheduled.

## Code structure

### Domain-split the remaining mega-files

*Status: open, opportunistic. Several files have grown past ~1.2K lines and want
decomposing by domain (pattern locked in
[ADR-0030](decisions/0030-domain-pure-code-organization.md), which already split
`types.ts` + `api.ts`). Do each as a behavior-zero refactor that preserves every
external symbol and keeps the test suite green — when next touching the file for a
feature, not as a standalone "refactor week".*

Current offenders (line counts 2026-07-24):

- `register/bank/BankRegisterPage.tsx` (~1990) — shell + row strategies + mutations + selection.
- `Db/Repositories/InvestmentTransactionsRepository.cs` (~1810) — partial-class split (`.Create` / `.Patch` / `.Delete` / `.Lots`).
- `Db/Repositories/TransactionsRepository.cs` (~1790) — partial-class split (`.Headers` / `.Postings` / `.Recon` / `.Merge`).
- `TxnRowEdit.tsx` (~1620) — mirror the investment-editor structure (per-field components + pure validation module + lifted draft hook).
- `settings/FeedConnectionsPanel.tsx` (~1200) — split into `ConnectionsList` / `AccountsDirectory` / `SyncRunsPanel` / `MappingWizard`.
- `SecurityDetailPage.tsx` (~1190) — split panels into siblings; keep dialogs with their owning panel.

---

## Test / CI speed

*Status: no known structural lever left — reopen only with a measurement.* The
Transactions-shard split (2026-07) cut the suite 613s → 401s, and building once
before running the shards in parallel (`scripts/ci-dotnet-shards.sh`) collapsed
~1000s of sequential execution to ~300s wall-clock. What remains is bound by test
execution across the shards (233-317s each), not by orchestration.

Before proposing another optimization here, read
[engineering-standards.md §9.1](engineering-standards.md#91-sharding-build-once-and-dont-assume-a-matrix-helps):
the pre-migrated-image idea was built, benchmarked and abandoned for zero gain, on
the strength of a bootstrap cost that had been assumed rather than measured.

## Systemic boundary testing for financial data (LOCKED — full audit)

**Why.** Two prod failures in one session (mig 180 `unit_cost` precision → 24dp
`cost_basis_sold` → .NET `decimal` overflow on read; and the snapshot OOM) both
slipped through because tests used kiddie-pool data ($100, 10 shares) that never
approached the magnitude/scale boundaries where money math actually breaks. For
financial data this is unacceptable — test data must mirror real magnitude AND
push scale/precision to the limits.

**Decision (2026-07-27).** Full audit: every test touching money, quantity, or an
aggregation/`decimal`-materialisation path gets a boundary case. Several PRs.

**Foundation (PR 1).**
- `tests/Api.Tests/Integration/Infra/Boundary.cs` — one source of truth for edge
  values, each documenting the limit it probes: `FractionalShares` (12dp → forces
  24dp `qty×unit_cost`), `LargeMoney` / `NearDecimalCeiling` (push NUMERIC→decimal,
  ~28-29 sig digits), `MaxScaleQuantity` / `MaxMoney` (fit `(25,12)` / `(19,2)`).
- `SyntheticLedger.AddBoundaryPositionAsync(...)` — seed a representative large
  fractional position in one call.
- Convention: financial paths use `[Theory]` with a `{ typical, boundary }` matrix
  so the boundary runs automatically. Documented in `engineering-standards.md`.

**Sweep (subsequent PRs, by theme).** Add a boundary case to every financial
suite: realized gains (done: `Large_fractional_position…overflow`), holdings /
net-worth (Overview), returns (IRR/TWR), in-kind transfer, cost-basis recompute,
snapshot round-trip, importer money mapping, register/resolved-transaction
aggregation, backup/restore money round-trip. Scope map: see the Explore pass
enumerating every `decimal`/`NUMERIC`/`Sum`/`GetDecimal` path.

**Complements the schema drift guards** (`SchemaDriftGuardTests`: unbounded-numeric
+ precision family) — guards catch the *column* side, boundary data catches the
*code-path* side. Together they close the overflow/precision class.

**Two end-to-end invariants still unasserted.** The reference-ledger harness
(`ReferenceLedgerInvariantsTests`, PR #414) ships four ledger-wide invariants —
every header balances, holdings = Σ open lots for quantity AND cost_basis,
realized_gains only on disposals, no negative holdings — plus a shape-coverage
guard. Two from the original end-to-end list did **not** ship and belong in this
sweep, since both are aggregation paths where magnitude bites:

- **Net worth reconciles** across the overview and the as-of valuation feeder —
  the two compute it by different routes and nothing asserts they agree.
- **Snapshot round-trips byte-for-byte** — asserted for restore *correctness*
  nowhere at ledger scale (restore *latency* is tracked under Snapshot restore
  performance below).

**Known blind spot, accepted: function return types.** The guard family covers
table columns only — a `RETURNS TABLE(... NUMERIC)` column is unconstrained even
when every underlying table column is properly typed, so the guard cannot see it.
The known instance is `holdings_market_value_as_of`
([172_holdings_value_as_of.sql:41-46](../db/migrations/172_holdings_value_as_of.sql#L41-L46)),
which declares `quantity NUMERIC, market_value NUMERIC`.

**Assessed and accepted — do not re-add as work.** Overflowing .NET `decimal` from
here needs a 24dp intermediate (12dp quantity × 12dp unit cost) to also reach
~28-29 significant digits, i.e. a position whose magnitude no plausible portfolio
reaches on a ~50-year horizon. Unlike migration 180 — a real failure, where the
*precision* widening alone pushed a live value past the ceiling — no reachable data
triggers this one. Documented so the gap is a known, deliberate acceptance rather
than an oversight; revisit only if a real overflow trace appears.

## Snapshot restore performance

*Status: effectively done — not worth further work at current scale. The derived-state
rebuilds are addressed (mig 188), the walk's cost is measured and demoted, and the
lane now asserts latency. Remaining ideas are recorded below for if that changes.*

**Headroom, which is why this is parked.** Restore measures ~65s on a
50k-transaction ledger against a 600s command timeout — roughly **9× headroom**, so
a ledger would need to approach half a million transactions before restore timed out
again. That is far past any personal-finance horizon, and the failure that started
this (a 30s default timeout, lifted in 0.36.8) cannot recur at these margins.
Optimising the reinsert path would be tuning something that already works with an
order of magnitude to spare. Revisit if a real ledger approaches ~200k transactions,
or if the lane's printed restore figure starts climbing.

**Done (mig 188).** A full-ledger restore ran ~27s. Both post-reinsert rebuilds
turned out to be avoidable:

- `recompute_holdings_cost_basis` was rebuilding what the payload already carried.
  `holdings` and `lots` are captured, so restore reinserted them and the recompute
  then overwrote them — its only unique output was `realized_gains`. That table is
  now captured too (it keys on `sell_leg_id`, a captured `txn_legs` row), and the
  recompute is gone from restore. Safe because restore refuses a cross-schema-version
  restore, so capture-time and restore-time derivation logic are identical by
  construction — a coupling mig 188's header documents, since relaxing the version
  guard would invalidate it.
- The per-account balance loop was doing dead work: a `DELETE` that could match
  nothing (restore already cleared the ledger's rows) and a starting-balance lookup
  that with a `0001-01-01` floor could only resolve to `opening_balance`, once per
  account — including every category account with no legs, so ≥103 no-op iterations
  per ledger after ADR-0091. Replaced by `fn_recompute_balances_for_ledger`, one
  set-based pass. Equivalence is pinned by `LedgerBalanceRecomputeEquivalenceTests`,
  which compares it against the per-account function over a ledger carrying every
  predicate that could diverge (override-shifted `posted_at`, hidden, merged-away,
  multi-split, non-zero opening balance, an account with no legs) — and was itself
  verified by removing the `PARTITION BY` and confirming it failed.

**Measured (the `Integration.Stress` lane now exists).** Numbers that
corrected the reasoning above, all on a seeded 50k-transaction / 200-holding ledger:

| Phase | Time |
|---|---|
| seed (set-based, 50k txns) | ~4s |
| snapshot create | ~3.4s (~85 MB payload) |
| **restore** | **~65s** |
| FIFO recompute, whole ledger, 200 holdings × 22 events | ~0.3s |
| FIFO recompute, whole ledger, 20 holdings × 500 events, 8k lots | ~2.3s |
| FIFO recompute, one position (what a txn write pays) | ~0.1s |

**The FIFO walk was never the bottleneck.** Mig 188's header originally claimed its
nested loops "are the ~27s"; measurement says otherwise, and the header now carries
the correction. Restore's time is the delete+reinsert of an ~85 MB payload. Mig 188
is still correct on its own terms — it stops re-deriving state the payload already
carries, removes provably dead work, and makes restore reproduce the snapshot — but
it was a modest saving sold as the fix.

Note also that breadth and depth are not interchangeable: the walk re-queries the
open-lot set per event, so cost grows with events × open-lots *within* a holding.
The 200-holding fixture (22 events each) understates it 7.7× versus 20 holdings
with 500 events each.

**Remaining.**

- **The payload reinsert path** — where restore's ~65s actually goes: restore
  `jsonb_populate_recordset`s ~100k legs plus every sibling table out of one jsonb
  document. *Parked on the headroom above, not blocked.* If it is ever picked up,
  start with a per-statement breakdown rather than a guess (the lesson from mig 188);
  candidates are `jsonb_to_recordset` with explicit column lists, COPY from a
  set-returning function, or splitting the payload per table so each insert streams.
- **The FIFO walk: deliberately NOT scheduled.** Hoisting its open-lot re-query out
  of the per-event loop was the expected follow-up; at ~0.1s per position on the
  write path, it does not currently justify surgery on the most
  correctness-sensitive function in the system. `FifoRecomputeCostTests` keeps the
  figures visible so this can be revisited if the shape of real ledgers changes.

**The stress/boundary lane (built).** The `Integration.Stress` namespace is
excluded from the sharded suite (`scripts/ci-dotnet-shards.sh`), so a
50k-transaction seed never sits in a PR's critical path; run it on demand with
`dotnet test --filter "FullyQualifiedName~Integration.Stress"`. Trade-off accepted deliberately: a latency regression will not fail
the PR that caused it, so run the lane after touching snapshots, the restore
function, the balance rebuild, or `recompute_holdings_cost_basis`.

`StressLedger` seeds set-based straight into Postgres rather than committing a
fixture: at 50k transactions the equivalent MD-export JSON would be tens of MB in
every clone, and this harness is about scale, not import fidelity (the
representative fixture covers shapes). Ids derive from the row index so runs are
reproducible, and consistency is established by calling the product's own
`recompute_holdings_cost_basis` over a seeded graph rather than by the seeder
asserting what it thinks the answer is — so the seeded ledger satisfies the same
invariants `ReferenceLedgerInvariantsTests` checks, by construction.

Restore now has its first latency assertion (120s budget against the 600s command
timeout — deliberately loose, since the lane runs on whatever hardware invokes it;
the printed timings are the real output). Both stress tests re-assert
header-balance and holdings-reconcile-with-lots at scale, which already earned
their keep: the lot-reconciliation assertion caught a fixture bug where buys spread
90 days apart ran decades past the disposals, leaving positions oversold because
the FIFO walk can only offer lots dated on or before a sell.

## Process notes

*(Empty. `mockups/` was deleted as planned — the surfaces it referenced have all
shipped, and it was the last place still carrying a real institution name.)*
