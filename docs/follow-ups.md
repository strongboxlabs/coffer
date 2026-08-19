# Follow-ups

The open-work backlog in one place: an ordered **Next** zone (what's shipping soon,
in ship order) followed by the unordered **Backlog** behind it, grouped by area.

**Lifecycle.** Add an item when it's surfaced. **Shipped items are deleted** — this
is a list of open work, not an audit log (git history is the audit log). When you
commit to shipping a backlog item next, **move** it up into Next; an item lives in
exactly one zone. Bigger-picture phase status lives in [README.md](../README.md)'s
Status section.

**Zones.**
- **Next (ordered)** — PR-sized slices intended to ship, in ship order. Each has a
  concrete shape (problem · approach · gating). Delete when it merges.
- **Backlog (unordered)** — grouped by area, not prioritised. Shaped or not, no
  schedule.

**Status legend.** Most items carry a short status line:
- *open* — surfaced, no schedule yet.
- *blocked on X* — waiting on a specific dependency.
- *partial* — partially shipped; the remaining work is what's described.
- *parked* — assessed, deliberately not scheduled, with the condition that would
  reopen it stated. Not the same as blocked: nothing is in the way.

---

## Next (ordered)

### Immediates

Pulled up out of Backlog on 2026-08-17, ahead of the CSV slices. The as-of valuation
work is finished — `holdings_snapshot` answers at any instant, cost basis included,
and the duplicate as-of feeders are collapsed to one implementation each. What
remains here is the same defect class, freshly re-evidenced.

### Tests that cannot fail — two slices

**The mutation sweep ran on 2026-08-17. Result: no survivors.** Thirteen targeted
mutations were applied across the financial paths, each reverted after one focused
test run:

| mutated rule | caught by |
|---|---|
| transfer_shares disposal gate removed | 5 tests |
| long-term boundary 1 year -> 1 day | 1 |
| merged-header exclusion removed | 1 |
| fee folding for is_trade_commission disabled | 1 (commission endpoints) |
| posted-at override ignored | 2 |
| seq tie-break dropped | 9 |
| category posting-role qualifier removed | 5 |
| excludedBrokerageCash forced to zero | 3 |
| trade-price tier never consulted | 11 |
| split back-adjustment of observed price removed | 1 |
| in-kind leg de-duplication removed | 3 |
| as-of basis swapped for current basis | 1 |
| importer minor-unit scaling /100 -> /10 | 31 |
| overview portfolio value dropped | 1 |

So the fear behind this item — that the financial assertions were weaker than they
looked — did NOT hold where it could be tested by breaking one rule at a time. That
narrows the work rather than expanding it.

One methodological note worth keeping: fee folding first read as SURVIVED because the
filter in use did not include the commission endpoint tests. A mutation surviving a
NARROW filter says nothing; it has to be re-run wide before it counts as a finding.

**What the sweep cannot reach.** A single-rule mutation cannot find a missing
CROSS-CHECK: break one side and that side's own tests fail, so the absence of an
agreement assertion never shows up. Both such gaps were confirmed absent by
inspection, and both are now written:

- **Net worth reconciliation** -> `Reporting/NetWorthReconciliationTests.cs`. The
  overview reads current balances and the holdings projection; the history series
  replays legs through the as-of feeder. Each had tests pinning its own numbers,
  which is precisely why a divergence between them was invisible. It failed on its
  first run with a 15,500 gap - the seed populated legs but not the projection, so
  the test found the fixture trap before it could find a real one.
- **Snapshot round-trip at ledger scale** -> `Stress/SnapshotRestoreLatencyTests.cs`
  now reads every money aggregate before and after the restore and asserts equality.
  It previously asserted latency and row COUNTS only: a restore that reinserted every
  row with a corrupted amount satisfied every assertion in the test.

Both slices below remain open; the sweep narrowed them rather than closing them.

#### Assertions that cannot fail
*swept 2026-08-18. Four instances found and fixed; detectors and their false-positive
rates recorded below so a re-sweep does not start from scratch.*

`Snapshot_payload_is_captured_server_side_in_content_json` asserted the captured
payload with `Assert.Contains("\"accounts\"", row.ContentJson!)` — the presence of a
*key*, which is equally true of `"accounts": []`. It would have passed against an
entirely empty snapshot, and it did: the test resolved `LedgerSnapshotsRepository`
straight out of a DI scope, so there was no request context, so `app.user_id` was
unset, so RLS filtered every in-scope table to zero rows. The test captured nothing
and asserted that it had captured something. Found only because migration 193 changed
where the payload lives and forced the assertion to be rewritten.

The general shape — asserting on structure rather than content, and exercising a
request-scoped path outside a request — is worth grepping for. Anything that reaches
an RLS-protected table must go through the endpoint (`AuthedClientAsync`) or the
service-role context deliberately; resolving a repository from a bare scope silently
sees nothing.

**The sweep ran on 2026-08-18.** Mechanical detectors over every `[Fact]`/`[Theory]`:
methods with no effective assertion, HTTP tests that never inspect body OR database,
and assertions on JSON *key* presence. Raw counts were useless — 16 "no assertion"
hits were all `Assert*` HELPER calls or a brace-parser tripping over C# raw strings,
and 85 "never reads the body" hits were overwhelmingly tests that verify the DATABASE
instead, which is the stronger assertion. Naive detectors here produce ~95% noise.

What survived was one coherent shape: **idempotency tests that assert only the repeat
call's status code**. Of 19 such tests, 16 verify resulting state; three did not, and
all three are now rewritten and mutation-verified — each mutation was caught by the
rewritten test and by NOTHING else, so each gap was real:

| test | was | mutation it now catches |
|---|---|---|
| `SessionsRepositoryTests.Revoke_is_idempotent` | zero assertions — called Revoke twice and passed if neither threw | dropping `RevokedAt == null` re-stamps `revoked_at`, silently rewriting when a session was revoked |
| `AccountGroupsEndpointsTests.Member_remove_is_idempotent` | removed a NON-member and asserted 204 — the no-op case named idempotency | dropping the account filter deletes every member of the group |
| `FeedConnectionsSyncTests.Delete_feed_mapping_is_idempotent…` | deleted a mapping that never existed, asserted 204, never read the columns | dropping the account filter unmaps every account in the ledger |

Note the pattern in all three: they exercised the *no-op* case, which is the easy half,
and never the repeat-on-real-state case. "Idempotent" names a property about what is
LEFT BEHIND, so a test that never looks at the leftovers cannot assert it.

Also fixed: the two remaining `factory.Services.CreateScope()` sites
(`SnapshotsTests` auto-snapshot pair) now build the repository over the service-role
context, the way `SnapshotJobHandler` does in production. They passed only because the
snapshot tables carry no RLS policies yet — so the pattern was both a wrong mirror of
production and a latent break waiting on "RLS on the snapshot tables" below.

#### Boundary cases for the remaining financial suites
*partial. The foundation shipped 2026-08-18 — this entry previously CLAIMED it had,
naming `Boundary.cs` and `AddBoundaryPositionAsync`, and neither existed. Two of eight
themes converted; six remain.*

Two prod failures came from tests using kiddie-pool data ($100, 10 shares) that never
approached the magnitudes where money math breaks, so financial paths now test a
`{ typical, boundary }` `[Theory]` matrix against
`tests/Api.Tests/Integration/Infra/Boundary.cs` — one source of truth for the edge
values, each documenting the limit it probes (12dp fractional shares, which force a
24dp `qty × unit_cost`; values near the NUMERIC→`decimal` ceiling; the `(25,12)` and
`(19,2)` column maxima). `SyntheticLedger.AddBoundaryPositionAsync` seeds a large
fractional position in one call. Realized gains carries its case already.

**Converted so far, and what it caught.** Realized gains (the existing case,
parameterised) and holdings / net worth. The second one FAILED on its first run, which
is the whole argument for the item:

`NetWorthReconciliationTests` asserts the overview and the history series agree. They
did — at whole-share magnitudes. At a 12dp fractional position they differed by 8
millionths of a dollar, because the two compute a position's market value at different
scales. The feeder bounds its output (`ROUND(v_qty * v_price, 4)`, mig 172:183, and
mig 200 by construction) because an unconstrained NUMERIC has to survive the trip into
`System.Decimal`, which throws rather than truncating. The overview multiplied in C#,
where a `decimal` product keeps the SUM of its operands' scales — `(25,12)` quantity
times `(19,4)` price runs to 16 decimal places. Whole shares have no fractional part to
round, so every existing fixture agreed by construction and the cross-check could not
fail.

Fixed by defining it once: `ReportingScale.MarketValue(quantity, price)` at 4dp, used
by both C# call sites. A sweep for the same shape found a second one —
`HoldingsRepository:153` computed the same unrounded product for the holdings list.
(`InvestmentTransactionsRepository:1665` also multiplies quantity by a unit cost but
already rounds to 2dp, and it is a lot cost rather than a market value, so it stays.)

Cost-basis recompute (`CostBasisRecomputeBoundaryTests`) passes at both magnitudes —
no finding, which is migrations 182 / 204 / 205 doing their job from the seeding side
rather than only from a schema guard. It also asserts the recompute is idempotent,
since a scale bug that rounded on each pass would drift further every run.

Returns (`ReturnsBoundaryTests`) passes at both magnitudes. Worth recording what it
cost to write, because two plausible expectations were both wrong: **TWR is annualized
over the covered days and returned as a FRACTION, not a percentage** — a 25% price
move over 144 days reads as `0.7605`, i.e. 76% annualized on a 365-day year, and
solving `ln(1+twr)/ln(ratio)` gives exactly `365/144` for both cases. And the two
boundary cases do NOT share a price ratio (1.25 vs 1.111), so "a return is scale-free
therefore both magnitudes must agree" is false as the fixture stands. The test derives
its expectation from each case's own ratio and the engine's OWN reported
`TimeWeightedCoveredDays`, so it survives a change to the window.

That second point is FIXED rather than noted: `Typical` moves 180 -> 200 and
`LargeFractional` 8.10 -> 9.00, both exactly 10/9, so **cross-magnitude invariance is
now a real property** — the same price move yields the same percentage at any size,
assertable without modelling the code under test. `ReturnsBoundaryTests` uses it, and
`BoundaryFixtureTests` enforces it so a case added later cannot silently break it (it
also checks each case's declared `Basis`/`Proceeds` against the arithmetic, so a test
asserting those is asserting a real figure).

In-kind transfer (`InKindTransferBoundaryTests`) passes at both magnitudes, and
finding out why it first didn't exposed a real limitation of the seeding helper:
**`AddBoundaryPositionAsync` writes no `lots` rows.** Migration 202 made the FIFO walk
pure, so `recompute_holdings_cost_basis` derives lots in memory and persists only
quantity, cost basis and realized gains — the `lots` table is written by the
investment-transaction WRITE PATH and by snapshot restore. A raw-seeded position
therefore has correct holdings and basis but nothing for a lot-consuming endpoint to
find, and `transfer_shares` rejects it with
`investment-txn-transfer-shares-insufficient`. The helper now documents this, and
tests exercising a lot-consuming endpoint buy through the API instead.

Snapshot round-trip (`SnapshotMoneyRoundTripTests`) passes at both magnitudes. It
lives in the NORMAL lane deliberately: the stress lane already compares money at
ledger scale, but `Integration.Stress` is excluded from both the CI shards and
preflight, so that one only runs when invoked by hand. This one runs on every push.
It guards against vacuity explicitly — the fixture must hold non-zero basis, lots and
realized gains, and the post-snapshot mutation must actually change them — so
`before == after` cannot hold because nothing ever happened.

Importer money mapping (`MoneyMappingBoundaryTests`) passes. `MinorUnitsToDecimal` —
the one line where every Moneydance money figure ENTERS the system — had no direct
test at all; the mutation sweep's `/100 -> /10` was caught by 31 tests only because the
resulting figures were absurd, not because anything asserted the scale. It now pins
the scale directly, and pins the importer's own ceiling: `long.MaxValue / 100` is
~92.2 quadrillion against the money column's ~99.9, so the headroom is real and
recorded rather than rediscovered.

The two test assemblies are independent, so `Boundary.cs` is **linked** into
`Importer.Moneydance.Tests` rather than copied — duplicating the edge values is how
they drift, and a case added for the API suites would otherwise silently not apply
where money enters.

Register aggregation (`RegisterAggregationBoundaryTests`) passes. `balance_after` is a
running SUM over every prior leg, so it is the one figure whose error ACCUMULATES —
invisible at three figures, compounding over a decade of rows — and the existing
coverage used -10 / -20 / -30, where any plausible bug still gives the right answer.
The balance is asserted after EACH row rather than only at the end, since a drift that
cancels out by the final row would otherwise pass.

Backup/restore money round-trip: **done, and it found the same gap the snapshot test
had.** `scripts/backup-restore-roundtrip.sh` built synthetic `parent`/`child` tables
with no money column at all and asserted row COUNTS — so it proved the
wipe-then-restore MECHANISM while a restore that reinserted every row with a corrupted
amount would have passed. It now seeds `NUMERIC(19,2)` / `NUMERIC(25,12)` values at the
column maxima and a 12dp fractional quantity, and compares a full-scale digest of every
value before and after. Verified live: a post-restore read truncating to 4dp fails it
with a printed diff.

**All eight themes are complete.** The sweep found two real defects (the `MarketValue`
divergence between the overview and the history series, and this one) plus one gap in
the test foundation itself (`AddBoundaryPositionAsync` writes no `lots`). On the evidence of theme two, expect each to surface
its own scale or magnitude disagreement rather than simply passing.

Two ledger-wide invariants that were listed here as unasserted SHIPPED on
2026-08-18: **net worth reconciles** between the overview and the valuation feeder
(`NetWorthReconciliationTests`) and **snapshot round-trips correctly at ledger
scale** (`SnapshotRestoreLatencyTests` now compares every money aggregate across the
restore, where it previously asserted latency and row counts only). Neither carries a
`{ typical, boundary }` matrix yet, so both remain in scope for the magnitude sweep
above even though the agreement itself is now pinned. The four invariants that
shipped earlier live in `ReferenceLedgerInvariantsTests`.

This complements the schema-drift guards: those catch the *column* side, boundary
data catches the *code-path* side.

**Known limitation, accepted.** The guards cover table columns only, so a
`RETURNS TABLE(... NUMERIC)` column is unconstrained even when every underlying
column is properly typed. The known instances are `holdings_market_value_as_of`
([172_holdings_value_as_of.sql:41-46](../db/migrations/172_holdings_value_as_of.sql#L41-L46)),
which declares `quantity NUMERIC, market_value NUMERIC`, plus the two batched feeders
that inherit the same declarations by construction: `holdings_market_value_as_of_set`
(mig 200) and `account_balance_as_of_instants` (mig 201). Reaching an overflow there
needs a position no plausible portfolio produces on a ~50-year horizon, so it is
documented rather than fixed.

### Next in line — MCP + reporting

The reporting and MCP surface was closed out on 2026-08-17 (returns engine, response
provenance, allocation reconciliation, the batched valuation feeders and the removal
of the TWR boundary cap). What remains in this area is ADJACENT rather than a
continuation of that work — none of it extends the returns engine — so it sits behind
the immediates above but ahead of everything still in Backlog.

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

### CSV Phase 5 — generic ingest provider (ADR-0031)

*Position note: this and Phase 6 were the whole of Next before 2026-08-17. They are
still committed work, placed last here only because the items above were explicitly
ranked ahead of them — not because the CSV slice was reassessed.*

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


### RLS on the snapshot tables
*open. Pre-existing; surfaced while writing migration 193, deliberately not bundled
with it.*

`ledger_snapshots` has no row-level security. Fifty-three tables in the schema enable
it; that one does not — and it holds a complete copy of every row of a ledger, the
same rows its source tables protect with RLS. `ledger_snapshot_parts` (migration 193)
follows the same posture rather than introducing a second, subtly different one for
the same data. Both are gated only by the API's `LedgerAuthorizer`, so anything that
reaches the database as `coffer_app` outside that gate reads any ledger's full
contents.

Adding RLS to both is a behaviour change to an existing table, needs a policy keyed
through `ledger_id` → `user_ledger_grants` like its siblings, and has to be checked
against the capture and restore functions (which run as the caller, not as
`SECURITY DEFINER` — so a policy that the request context does not satisfy would make
snapshots silently capture nothing, exactly the failure mode described under
"Assertions that cannot fail").

### Secrets handling

#### Database credentials still travel by environment variable
*open (surfaced 2026-08-06, alongside ADR-0092).*

ADR-0092 moved the master KEK out of `COFFER_MASTER_KEK_BASE64` into a file (and
ADR-0094 removed the variable outright),
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

#### The backup passphrase ceremony is lighter than the master key's
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

### Localisation

#### Per-user language/culture + a ledger main currency

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

### SPA / register

#### Tax / transaction date — systemic surface

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

### Bank feeds

#### Bulk security-mapping step in the OFX import dialog

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

### Register surface

#### Register non-date scroll affordance

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

#### Splits editor — optimization / refactor

*Status: open. Requested 2026-07-11; shape TBD.*

The multi-split transaction editor wants a perf + code-structure
pass. Scope to be defined against the current editor.

#### Bulk Categorize / Tag actions

*Status: blocked on the bulk override / categorisation write endpoints.*

The bulk-action footer in `register/bank/BankRegisterPage.tsx` reserves `Categorize…`
and `Tag…` buttons (rendered disabled today). Wiring waits on the
bulk override / categorisation endpoints — neither exists yet.
Once those API surfaces land, the buttons gain handlers that
PATCH each selected header in the same optimistic-cache pattern
the recon-status bulk buttons use today.

---

### Sidebar

#### Folder accounts (sub-grouping within types)

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

#### Sidebar tab reorder (drag-to-rearrange)

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

#### Remember last-active sidebar tab across sessions

*Status: open. Defer per design discussion.*

`AuthedSidebar`'s `activeGroupId` resets to "All" on every
refresh. The user explicitly chose "All by default, no
localStorage drift" for v1; the deferred path is a
`user_preferences` table (or JSON column on `users`) that holds
per-(user, ledger) UI state including last-active tab,
collapsed-section state, etc. Land when there's more than one
preference worth persisting.

#### Drop the vestigial counters on `sync_runs`

*Status: open cleanup. Slice 2c.1 left `txns_merged` / `txns_queued` /
`txns_skipped` in place on `sync_runs` (always 0 — they're
from the pre-2c merge / staging pipeline). Migration 044
dropped the sibling vestigial tables (`pending_transactions`,
`merge_candidates`, `merge_rules`, `transaction_rules`); the
sync_runs counters survived because they live on a table we
still use. Drop them in a future cleanup.*

#### Sync activity log retention

*Status: open. Slice 2c.1 keeps every `sync_runs` row forever. At
one sync/day that's ~365 rows/year/connection — fine through
the personal-use horizon. When daily polling lands (background
worker, not yet built) or a multi-tenant deployment shows up,
re-evaluate. Likely shape: keep last 90 days verbose, roll
older runs into a per-month summary row; or simply LIMIT the
list query and never paginate beyond N. No automation today.*

#### Scheduled-sync worker (slice 2d-ish)

*Status: open. The scheduler framework exists (`scheduled_jobs` mig 136 +
`IScheduledJobHandler` / `IGlobalScheduledJobHandler`, already driving daily
backups + quote refresh); feed sync is manual-trigger only — no feed-sync job
handler is wired. This item is adding one.*

Today every `sync_runs` row carries a `triggered_by_user_id`; a background
daily-poll handler would land rows with NULL there. The activity panel + counters
already accommodate this — no UI change needed when the worker ships.

#### Persist failed SimpleFinException runs more granularly

*Status: open. Slice 2c.1 captures the exception message verbatim
in `sync_runs.error_message`. Future polish: capture the
upstream HTTP status code + a redacted response body for the
diagnostics-fast-path case where the bank breaks the v2
contract. Probably as a JSONB column or a child table parallel
to `sync_run_errors`.*

#### Bulk Approve via ADR-0024 selection

*Status: slice 2d. Right-click → Approve handles single rows
today; the ADR-0024 bulk selection machinery already exists
and would extend cleanly to a bulk-approve endpoint
(`POST /api/ledgers/{id}/transactions/bulk-approve` with a
`SelectionRequest` body).*

#### Rule-based auto-categorization on sync

*Status: slice 2d. MD's screenshot shows the yellow
pending rows already carry categories (Insurance:Automobile,
Hobbies-Leisure:Entertaining, etc.). That comes from MD's
rules + payee memory. Today every sync row in Coffer lands on
the per-ledger "Uncategorized" counterparty. Build a small
rules engine (payee-substring → category, with priority +
on/off toggle) and apply it in `SimpleFinSyncService` before
the leg insert. Approve flow stays unchanged.*

---

### Investment data

#### Per-security filter on the register Toolbar

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

#### A5 — Edit Lots affordance

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

#### Stock-split lot fan-out

*Status: open. Triggers when A4 ships the `split` action button.*

A4's editor will offer `split` as one of the 8 actions, but the
*real work* of a corporate split is splitting every open lot
(e.g. 2-for-1: each lot's `quantity` doubles, `unit_cost` halves;
`acquired_at` stays so short-vs-long-term holding period is
preserved). This is non-trivial enough to deserve its own slice
once the editor is in place.

---

### Multi-user collaboration

#### SSE notifications for live edits across users on the same ledger

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

### Observability

#### Notify on missed or failed scheduled jobs
*open. Migration 194 records the state; nothing surfaces or announces it.*

The snapshot/backup outage ran for roughly two days without a single signal. The
only thing a human could have noticed was an unrelated-looking 500 from
`/oauth/authorize`, and the only record was `LogError` lines inside a container.
Snapshots stopped for ~68 hours and backups for ~47, both silently.

Migration 194 added the raw material — `consecutive_failures`, `last_error`,
`last_failure_at` on both `scheduled_jobs` and `global_scheduled_jobs`, and
`SchedulerRunner` disables a job after five consecutive failures. None of that is
surfaced or pushed anywhere. What is missing:

- **Surface it.** The schedule panels should show last outcome, consecutive
  failures and `last_error`, and mark an auto-disabled job distinctly from one a
  user turned off — right now those are indistinguishable in the UI.
- **A missed-run check, not just a failed-run one.** Failure counting only fires
  when a handler is dispatched and throws. A job that never ran at all — worker
  dead, container down, `next_run_at` NULL, row disabled by the threshold — has no
  failures recorded and looks healthy. The check that matters is "`last_run_at` is
  older than the schedule implies", which catches both.
- **Announce it.** Email via the existing notification path, or the health endpoint
  reporting degraded so an external monitor sees it. A backup that has not
  succeeded in 48 hours deserves to interrupt someone; anything that only writes
  to a log we do not read is not a notification.
- **Consider a floor on backup age** as the highest-value single alert — that is
  the one whose absence actually costs data.

#### Prod OTLP tracing exporter

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

### Code structure

#### Domain-split the remaining mega-files

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

### Performance

#### Snapshot restore — the payload reinsert path
*parked, not blocked. Reopen if a real ledger approaches ~200k transactions, or if
the stress lane's printed restore figure starts climbing.*

Restore measures ~65s on a 50k-transaction ledger against a 600s command timeout —
roughly 9× headroom — and the time goes to the delete+reinsert of an ~85 MB jsonb
payload (`jsonb_populate_recordset` over ~100k legs plus every sibling table), not to
the derived-state rebuilds, which were measured and removed (migration 188). The FIFO
walk was never the bottleneck: ~0.1s per position on the write path.

If it is picked up, start with a per-statement breakdown rather than a guess — that
was the lesson of migration 188, which was sold as the fix for something it barely
moved. Candidates: `jsonb_to_recordset` with explicit column lists, COPY from a
set-returning function, or splitting the payload per table so each insert streams.

**Running the measurements.** The `Integration.Stress` namespace is excluded from the
sharded suite, so a 50k-transaction seed never sits in a PR's critical path — which
also means a latency regression won't fail the PR that caused it. Run it on demand
after touching snapshots, the restore function, the balance rebuild, or
`recompute_holdings_cost_basis`:

```bash
dotnet test --filter "FullyQualifiedName~Integration.Stress"
```

It carries a deliberately loose 120s restore assertion (the lane runs on whatever
hardware invokes it; the printed timings are the real output) and re-asserts
header-balance and holdings-reconcile-with-lots at scale.
