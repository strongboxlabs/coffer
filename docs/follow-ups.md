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

*Re-ranked 2026-08-28 to the maintainer's stated feature priorities; the seven
numbered slices below are that order verbatim. Two things sit outside the ranking and
say so: the engineering residue immediately below, which is nearly closed and commits
no new work, and an unranked tail at the end. Items promoted from Backlog were MOVED,
not copied — an item lives in exactly one zone.*

*What the ranking changed: **Budgets went from first to last** and **CSV import from
last to second**. Where that order fights a dependency, the dependency is stated in the
slice rather than silently reordered.*

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

#### Mutation-checking a new assertion is a habit, not a rule — and the habit keeps losing

*open. Surfaced 2026-09-01 by three of my own vacuous tests in a single session, two of
which I wrote AFTER the sweep above.*

The sweep on 2026-08-18 found four assertions that could not fail and fixed them. It did
not stop new ones being written. Three more appeared on 2026-09-01, and the interesting
thing about them is not that they existed — it is how they were caught.

| the test | why it could not fail | caught by |
|---|---|---|
| `ScheduleControl.test.tsx` — "says nothing alarming about a job a person simply turned off" | the only case in its block with no `await`, so both `queryByText` calls ran on the pending frame where `ScheduleHealth` short-circuits and the DOM is empty | an adversarial review of the release diff, then mutation. It had already SHIPPED, in #504 |
| a DST endpoint test — PUT a 02:30 schedule, expect 200 | the endpoint passes `DateTime.UtcNow`, so the next 02:30 is almost never the spring-forward date | mutation, minutes after writing it |
| the service-context recompute tests, as first planned | written over `PostgresFixture.NewServiceDbContext()`, which builds its own bare options independent of the production factory | an adversarial review, BEFORE they were written |

**None of the three would have failed review.** Each reads as a normal, well-named test
asserting a real property. The first shipped. What they have in common is that the
assertion is NEGATIVE — "this string is absent", "this returns 200", "no exception" —
and a negative assertion is true of an empty world, so it passes whenever the code under
test never actually ran.

**A periodic sweep cleans up; it does not prevent.** Between the sweep and today the
repo gained two more, at roughly the rate the sweep removed them.

**What to write into `docs/engineering-standards.md`.** The rule is cheap to state and
cheap to follow: *a new assertion is not finished until it has been observed to fail.*
The lightweight form is naming, in the PR body, the one-line mutation each new test
catches — which this repo's commit messages have converged on informally already, and
which is exactly the discipline that caught the second and third rows above.

Scope it deliberately, or it becomes ceremony nobody does:
- Assertions on NEW behaviour, not every touched test.
- **Negative assertions always** — `queryBy*`, `Assert.Null`, `DoesNotContain`,
  `Assert.Empty`, "expect 200". These are the whole population above.
- Not required where the test already fails first and is made to pass, which is the same
  evidence arrived at from the other side.

**A lint rule beats a habit for the React half.** The `ScheduleControl` shape is
mechanically detectable: a React Testing Library test whose body contains a `queryBy*`
negative assertion and no `await` / `findBy*` / `waitFor` before it is asserting against
an unrendered component, essentially always. That is one custom ESLint rule, it would
have caught the case that shipped, and unlike a written rule it cannot be forgotten. Worth
pricing before writing more prose.

The C# half has no equivalent one-liner — "would this pass against unmodified code" is
undecidable in general — so that half stays a stated rule plus the PR-body habit.

**Related, and the reason this is worth doing rather than noting:** every entry in this
section was found by something other than the test suite. The suite is where these are
supposed to be caught, and a can't-fail assertion is precisely the defect the suite is
blind to by construction.

#### Boundary cases for the remaining financial suites
*partial — the theme conversions are DONE; the residue is named at the end of this
entry. This status line read "Two of eight themes converted; six remain" until
2026-08-28, while the body two screens below said all of them were complete. The body
was right: themes five through nine are committed. Corrected rather than deleted,
because the entry still owns open work.*

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

---

### 1 · Reminders

#### Slice complete: auto-post, un-skip, estimated amounts

*closed 2026-09-03 by #512 / #513 / #514 / #516 / #517, released in 0.72.0, 0.72.1 and
0.73.0. Slice 1 has no open work.*

All three entries are done and their text is deleted rather than annotated — ADR-0097
carries the auto-post decisions, and a closed entry restating them is a second copy that
will drift. One heading stays so the slice does not read as untouched.

Four things learned here that are NOT in the ADR, because they are about how the work
went rather than what was decided:

**The first shape of auto-post reproduced the defect it was built to remove.** It shipped
in 0.72.0 needing a per-reminder option AND a separate per-ledger switch, so ticking the
option did nothing, silently — exactly as before, one level up. Found by the maintainer on
first real use, not by any review pass, and fixed in 0.72.1 by making the tick the whole
opt-in. A feature whose point is "this should not fail silently" deserves someone actually
trying it before it is called done.

**Every guard worth having was already doubled**, which made single-mutation tests
impossible for three properties: skip-respected, no-cross-ledger-write, and the auto-post
scan's ledger predicate. Each is enforced at two or three layers, so removing any one
leaves every test green — and for ledger scoping, removing TWO still does, because
`FireAsync` resolves by `(id, ledgerId)`. The tests record that rather than deleting real
guards to make a mutation visible. Expect it here; defence in depth and per-guard test
isolation are in tension, and the depth is worth more.

**Two exclusions collapsed into one predicate** on estimates, and the collapse came from
the maintainer, not the design. Splits have no single amount to estimate; a loan payment
is computed from its terms and current balance, which an average would only degrade. As
one eligibility check they are cheap; as two special cases they would have been a rule to
re-derive at every call site.

**Scope questions belong to the maintainer.** The design pass proposed auto-post SKIP
estimated reminders on the grounds that a guess should not be committed unattended. That
was overruled — "let the user worry about it; if it's a problem they'll uncheck it" — and
the simpler product is the shipped one. The design pass was restarted rather than
patched, because two of its stated constraints had become false and it would otherwise
have answered the wrong question well.

### 2 · CSV import

*Promoted from last to second by the 2026-08-28 ranking. Phase 5 is the generic path
and Phase 6 the hand-coded ones; the ranking now names the two institutions that
motivated the slice, which the entry previously left abstract.*

#### CSV Phase 5 — generic ingest provider (ADR-0031)

*Position note: this and Phase 6 were the whole of Next before 2026-08-17. They are
still committed work, placed last here only because the items above were explicitly
ranked ahead of them — not because the CSV slice was reassessed.*

`GenericCsvProvider` reading a `feed_csv_mappings` config (column map + date
format + sign convention + header rows), extending the same `IngestOrchestrator`
as SimpleFIN / OFX / QIF. The column-mapping wizard UI lands here. Resolves the
CSV slice (file upload, per-institution mappings).

**No dedup, decided 2026-09-04.** The earlier "hash-based `external_id`" plan is
withdrawn. CSV rows carry no issuer-assigned id, so per-row identity has to be inferred
from content — and content cannot separate two genuinely identical transactions (same
date, same amount, same merchant). A content hash silently collapses them and eats a real
row; folding in the row ordinal silently duplicates the whole file the moment a download
window shifts. Both fail silently, in a ledger reconciled against real balances, and the
juice is not worth the squeeze.

What replaces it is UNDO, shipped ahead of the slice: mig 221 stamps
`txn_headers.ledger_operation_id` with the import that wrote each row, and
`POST /ledger-operations/{id}/undo-import` hides exactly that set (`?dryRun=true` counts
first). Exact, no inference, and `bulk-unhide` is its inverse.

Note for whoever builds the provider: "no dedup" does NOT mean leaving `external_id`
NULL. `ck_txn_headers_external_id_for_non_manual` (mig 109) is
`external_id IS NOT NULL OR origin = 'manual'`, and an import writes
`origin = 'file_import'` — so every imported row MUST carry one. Emit a deliberately
non-matching value (unique per import) rather than omitting it. A useful side effect:
that keeps imported rows on the soft-hide side of bulk delete, so an undo can never
destroy one.

#### CSV Phase 6 — per-institution ingest providers (ADR-0031)

Hand-coded providers for institutions whose format defeats the generic path (a
workplace 401(k) plan expected first). Each is a sibling `IFileProvider`
registered alongside the generic one; the orchestrator dispatches purely on the
provider key.

**Named targets (2026-08-28).** **Macy's** and the **Fidelity brokerage CSV**, in that
shape: Macy's is a card statement, so bank-shape rows and the better Phase 5 exercise;
the Fidelity brokerage CSV is investment-shape and the likelier Phase 6.

**The 401(k) is NOT a CSV problem — and it is closer than this slice.** Fidelity
NetBenefits exports QIF, and Coffer's QIF importer already exists and was written for
exactly that dialect: `QifFileProvider`'s own remarks say "a QIF file (at least the
workplace plan dialect) carries no account header". What stops it is one gap, already
recorded under **Bank feeds › QIF drops investment actions the OFX provider already
maps**: `CONTRIBX` and `WITHDRWX` are skipped with "cash transfers inside an investment
section aren't imported in this slice", and on a 401(k) statement the contributions are
most of the file.

**Shipped 2026-08-28.** The four arms now route to the bank shape exactly as the OFX
provider routes `INVBANKTRAN` (`Action: null`, wire-shape), and `RtrnCap` classifies as a
cash distribution like `RETOFCAP`. So the workplace-plan route needs no new provider, no
mapping wizard and no schema, and this slice keeps only Macy's and the Fidelity brokerage
CSV.

**Confirmed against a real NetBenefits export (2026-09-11).** The maintainer reports the
QIF route works: "NetBenefits provides QIF and works fine". The workplace-plan question
is closed — no new provider, no mapping wizard, no schema. What remains of this slice is
the brokerage CSV below.

### 3 · Splits

#### The split edit screen

*Status: open, and now shaped. Requested 2026-07-11 as "perf + code-structure pass,
scope TBD"; the 2026-08-28 ranking makes it a product slice rather than a refactor.*

The entry sat unshaped for seven weeks because it was recorded as a refactor, and a
refactor with no stated defect has no acceptance criterion. Ranked as a feature, it
gets one — and the two known concrete problems are the place to start:

- `TxnRowEdit.tsx` is ~1620 lines and is one of the six files listed under **Domain-split
  the remaining mega-files**. That entry's own rule is to decompose *when next touching
  the file for a feature*, and this is that moment. Two commits in one PR: a
  behaviour-zero decomposition whose gate is the existing test file staying green with
  no assertion edits, then the feature.
- Per-posting tags land here (below), which is new UI inside the same editor. Doing the
  decomposition first is what stops that landing in a 1600-line file.

#### Per-posting tags

*Status: open, ADR first. **Un-parked 2026-08-28** — the ranking commits to the feature,
which reverses the standing recommendation to delete the placeholder.*

The editor already renders the promise: `<TagsPlaceholder hint="Per-posting tags coming
soon" />` beside every posting in
[TxnRowEdit.tsx:1301](../src/Web/src/routes/ledgers/TxnRowEdit.tsx#L1301). Until this
ranking the honest move was to delete that control, because holding it meant shipping a
permanent "coming soon" against an ADR nobody was scheduled to write. The ranking
schedules it, so the control stays and the ADR gets written.

**The ADR is not optional and it is not small.** No `txn_leg_tags` table exists; ADR-0009
put tags at transaction level and stands unamended, and ADR-0025's "Open questions" now
reads "None", so the deferral has fallen off the ADR trail entirely. The schema shape is
already forced by things that will fail closed:

- the table must carry `ledger_id NOT NULL` — `fn_snapshot_write_part` chunks on a
  generic `WHERE t.ledger_id = $1`, so a snapshot-captured table without that column
  cannot be captured at all;
- it must enable RLS with the flattened `user_ledger_grants` policy, like
  `txn_header_tags`;
- `SchemaDriftGuardTests.Snapshot_payload_classifies_every_ledger_scoped_table` fails the
  moment the table exists, so the snapshot decision lands in the same PR — five snapshot
  bodies plus `fn_ledger_delete`.

Two existing entries resolve when this ships: the importer's per-leg tag collapse (see
**Moneydance import**), and the inert placeholder above. A rental-property split that
imports as `{Property A, Property B, Property C}` on one header becomes attributable per
posting — which is the actual user-facing point of the whole item.

### 4 · Home screen — a richer ledger dashboard

*Status: open. Surfaced 2026-08-28 by the feature ranking. Extends what exists rather
than replacing it — confirmed as the ledger dashboard, not a cross-ledger landing page.*

`LedgerDetailPage` is already the home screen and already configurable: five widgets
(`net-worth`, `accounts`, `investments`, `upcoming`, `activity`), reorderable and
toggleable, persisted per ledger through the `dashboard` preference and edited in
`DashboardLayoutPanel`. Accounts is pinned visible; the rest are opt-in. So the work is
not "build a dashboard" — it is that five stacked widgets is a list, not a screen.

Worth settling before building:

- **Which widgets are missing rather than thin.** Budgets-at-a-glance and a cash-flow
  strip are the obvious candidates, and both are downstream of slices 6 and 7 — so a
  home screen ranked *above* Reports and Budgets can only build the frame for them now.
- **Layout, not just order.** Today the preference stores `{ key, visible }` and renders
  a stack. Widths or a grid mean the stored shape changes, which is a preference
  migration, not a CSS change. Decide it once, here.
- **What the screen is FOR.** A summary surface and an attention surface are different
  products, and the widget set gives no opinion: `upcoming` and `activity` are
  attention, `net-worth` and `investments` are summary. Picking one makes every later
  widget decision easy and picking neither makes each one an argument.

### 5 · Accounts organization

*Both items promoted from the Sidebar backlog area by the 2026-08-28 ranking. They are
one slice: the persisted-tab work is a `user_preferences` shape, and folders are the
thing most likely to make that preference worth persisting.*

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

#### Remember last-active sidebar tab across sessions

*Status: open. Defer per design discussion.*

`AuthedSidebar`'s `activeGroupId` resets to "All" on every
refresh. The user explicitly chose "All by default, no
localStorage drift" for v1; the deferred path is a
`user_preferences` table (or JSON column on `users`) that holds
per-(user, ledger) UI state including last-active tab,
collapsed-section state, etc. Land when there's more than one
preference worth persisting.

### 6 · Reports

*The ranked position of the reporting slice. Its three sub-items — transactions;
cash flow / income-expense / net worth; investment portfolio and performance — are
already the canned set described below, so the ranking confirms the entry rather than
changing it.*

**Two dependencies this position does not remove.** The reports layer is specified to
sit on the MCP `ReportSpec` / `ReportingRepository` layer rather than a parallel one, so
it inherits whatever that layer can express. And it is worth very little without
realistic data — see **Realistic anonymized demo import** in the unranked tail, which
has to precede it. The current demo sample is all-uncleared and investment-only, which
has already misled reconciliation work once.

#### Canned + memorized reports (reuse the MCP reporting layer)

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

### 7 · Budgets + budget-vs-actual

*Status: open. The next backend/product slice after historical valuations (PR B, shipped).*

Whole subsystem: schema (amount/category/period) + API + UI + variance report +
MCP exposure.

---

### Unranked by the feature list

*Two entries the ranking does not cover. Neither is deprioritised — they are inputs to
the ranked slices rather than slices themselves.*

#### Realistic anonymized demo import

*Status: open. Ranked implicitly by slice 6, which needs it.*

Synthesize-on-structure: read the real export ONLY for structure (account-graph
topology, txn shapes, stat/splittype distributions) and emit synthetic
names/payees/tickers/account numbers with jittered amounts and shifted dates — nothing
real copied, so there is no PII to leak. Deterministic C# generator (project stack, no
Python); decide replace-vs-new, update `DemoSampleImportTests`, update provisioning.

The reason it moved next to Reports: the current
`data/samples/moneydance-export-demo.json` is all-uncleared and investment-only, so it
exercises neither reconciliation nor a cash-flow report. A reports slice tested against
it would pass while being wrong for every real ledger. **Also gated behind the importer
work** — `DemoSampleImportTests` asserts against this file's structure, and the
Moneydance payee/memo slice changes what the importer extracts from it.

#### MCP and reporting adjacencies

*Status: open, no schedule.*

The reporting and MCP surface closed out 2026-08-17 — returns engine, response
provenance, allocation reconciliation, the batched valuation feeders, the removal of the
TWR boundary cap. What remains in that area is adjacent rather than a continuation, and
none of it extends the returns engine. It has no ranked position because the feature
list does not name it; it collides with nothing, so it can run alongside anything.

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

### Data integrity

*Surfaced 2026-08-28 by a sweep for work recorded outside this file. Each item is a
correctness obligation someone deferred deliberately, with the deferral written into a
migration header or a code comment and nowhere else.*

#### Migration 209 fixed the realized-gains writer and left every existing row wrong

*partly done. The prompt shipped; the per-ledger repair state has not.*

[209_realized_gains_round_once.sql:45-50](../db/migrations/209_realized_gains_round_once.sql#L45-L50)
says it plainly: "NOT BACKFILLED HERE. Existing rows keep their double-rounded values
until something recomputes them." The reasoning holds — a blanket recompute at migration
time would rewrite holdings and lots for every pair, a far larger blast radius than the
cent it corrects — but the consequence was left unowned.

The same header measures it on real data: on a 42k-transaction, 586-disposal ledger three
rows disagreed with a fresh walk. So on every upgraded install, tax-relevant realized-gain
figures are a cent off and the consistency report pins `realized_gains` unhealthy until
someone repairs it PER LEDGER.

**The prompt half is done.** An unresolved `consistency.drift` notification renders "Check
and repair", linking to the General tab with `?check=true`, which runs the check on arrival
and offers the per-projection repair inline. Finding Settings, then Maintenance, then the
button, unaided, is no longer the only route to the fix.

**The state half is open.** Nothing records which ledgers have been repaired, so "done"
stays invisible: an operator with several ledgers cannot tell one that was repaired from
one that was never opened, and a report expected to be unhealthy on some unremembered
subset still trains people to ignore the one signal that matters. Wants a per-ledger repair
state.

#### The consistency checker skips the one projection written by raw SQL

*open. Documented as a deliberate omission in the DTO.*

[LedgerConsistencyRepository.cs:52-62](../src/Api/Db/Repositories/LedgerConsistencyRepository.cs#L52-L62)
checks four projections — balances, holdings, realized gains, posting counts. The fifth,
trade-derived `security_prices`, is left out with a stated reason
([LedgerConsistencyDtos.cs:72-78](../src/Api/Contracts/LedgerConsistencyDtos.cs#L72-L78)):
a trade leg seeds a price row, but the per-day source-priority rule makes a MISSING row
legitimate whenever a manual or fetched price already owns that day, so a naive check
reports drift that is not there.

That was the right call and it still leaves the hole where the tool's own justification
points. The checker exists because a raw-SQL scrub silently desynced a projection and the
register showed wrong figures for months; the uncovered projection is the one written by
migration 177's rank-gated bulk INSERT — a raw-SQL path that bypasses the EF interceptors
— and it feeds valuation, allocation and returns. The check is writable, just not naively:
compare against what the source-priority rule implies for each day, not against row
presence.

#### The `ledgers` LEK columns never got their NOT NULL follow-up

*open. Promised by migration 035, 180 migrations ago.*

[035_ledger_encryption_key.sql:16-21](../db/migrations/035_ledger_encryption_key.sql#L16-L21)
ships the columns nullable and states that "a subsequent migration sets NOT NULL once
backfill is verified complete". Backfill is still lazy-only (`EnsureWrappedLekAsync`, on
first secret access), so a ledger that has never written a secret can carry a NULL
`wrapped_lek` indefinitely — and both `KekRotationService` and `KekReconciliationService`
filter `WrappedLek != null`. A never-backfilled ledger is therefore skipped silently by
rotation and by reconciliation, both of which then report success. Needs the eager
backfill the header calls "a follow-up polish", and then the constraint.

#### A snapshot cannot be restored across a schema version

*open, and compounding. ADR-0037 Phase 1.*

Restore refuses when a snapshot's `__schema_migrations` version differs from the live
database
([LedgerSnapshotsRepository.cs:293-297](../src/Api/Snapshots/LedgerSnapshotsRepository.cs#L293-L297)),
returning `SchemaVersionMismatch`. So every migration invalidates every snapshot taken
before it, and the pre-upgrade snapshot — the one a user takes precisely BECAUSE an
upgrade is risky — is the one that cannot be restored once the upgrade lands. They find
out at restore time.

It compounds rather than sitting still: migration 188 made the guard load-bearing for
correctness rather than mere compatibility, dropping the realized-gains recompute and
capturing the derived rows in the payload, justified explicitly by "restore REFUSES a
cross-schema-version restore ... so the derivation logic cannot have changed in between".
Lifting Phase 1 now means reworking what 188 captured. The cheap half — say so in the UI
at capture time, so the artifact stops reading as insurance it is not — needs no ADR.

---

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

#### The same transaction arriving from two sources imports twice

*open. Surfaced 2026-09-12 by the first real Fidelity brokerage import, against an
account whose 401(k) rollover had already been imported from NetBenefits QIF.*

Nine rollover legs and a $51,129.88 core-account purchase imported a second time,
matching existing rows to the cent. Nothing detected it, nothing warned, and the
duplicates sit in the register beside the originals.

**This is the "no dedup" decision (2026-09-04) meeting a case it did not consider.**
That decision is still right for what it examined: CSV rows carry no issuer-assigned
id, content cannot separate two genuinely identical transactions, and UNDO is an exact
recovery needing no inference. What it assumed was ONE source per account — where
re-importing the same file is the only overlap, and undo covers it.

A rollover has two sources by nature. The 401(k) plan describes the money leaving
(NetBenefits QIF) and the brokerage describes it arriving (Fidelity CSV), and both are
legitimate imports the user wants. Undo does not help: neither import is the mistake.

**What makes it hard, in the order it bites.**

- *The dates disagreed*, by one day — Fidelity's Run Date is when it processed, the
  plan's is when it happened. FIXED: the shim now reads the `as of <date>` Fidelity
  splices into the Action text. That removes a red herring; the rows now land on the
  same day as their twins, which makes the duplication visible rather than solving it.
- *The amounts match to the cent, and legitimately so.* Nine rollover legs of
  $4.40 / $146.57 / $437.45 … are nine separate movements that happen to arrive
  together. Any content hash collapses genuine repeats.
- *The two sides are not the same shape.* The plan's side is a withdrawal from a
  401(k); the brokerage's is cash arriving with no counter-account, because a brokerage
  file cannot know where the money came from. They are the same EVENT, not the same row.

**Not obviously a dedup problem at all.** The register already shows the right answer
for the existing rows: they carry a counter-account (`Fidelity Koniag 401(K)`) and read
as `Xfr`. The imported ones have no counterpart and never will from the file alone. So
the shape of a fix may be "offer to match an imported row against an existing one and
merge them into a transfer" rather than "refuse the import" — which is a reconciliation
surface, not a parser change.

**Not scheduled.** It needs the maintainer's call on which of those it is, and it is not
worth guessing at: getting it wrong silently either drops real transactions or leaves
paired ones unpaired, and both are worse than the visible duplicates that exist today.

#### A 422 from an import endpoint loses the problem list it carried

*open. Surfaced 2026-09-10 by an audit of the import flow; the rest of that audit's
findings are fixed.*

`previewCsv` / `importCsv` refuse an invalid mapping with **422** whose body is the
endpoint's own `CsvMappingValidationResponse` — the full list of problems, each with a
key path and a line. That body is not ProblemDetails, so the shared `request` helper
cannot read it, `ApiError` carries nothing, and `errorMessage` falls back to the status
text. The user clicks Continue on a broken document and is told **"Unprocessable
Entity"**, with the answer sitting unread in the response body.

Deliberately not folded into the import-flow fixes: the honest repair is in the shared
client (teach `request` to surface a typed 422 body, or have these endpoints answer with
ProblemDetails plus an extension), and that reaches every caller rather than this dialog.

Partly masked today — Check validates against the dedicated endpoint and renders the
same list properly, and Continue is dead while the document is empty — so the bad path
needs an invalid-but-non-empty document. It is still the one place in the flow that
answers a real question with a status code.

Related, from the same audit and also open: the file-read *rejection* path
([CsvMappingStep.tsx](../src/Web/src/routes/ledgers/register/shell/CsvMappingStep.tsx))
now reports rather than swallowing, but only the empty-file branch is pinned by a test —
`File.text()` rejecting is not simulated anywhere.

#### Dark mode

*open. The token layer was built for it; the swap never happened.*

ADR-0021 Rule 4 defers dark mode, and [index.css:14](../src/Web/src/index.css#L14) carries
the matching promissory note — dark mode as "a future ADR + PR that overrides these tokens
inside a `[data-theme="dark"]` block". No such block exists, and no `prefers-color-scheme`
rule exists anywhere in the SPA. The expensive half was paid up front, since every colour
is already indirected through a token, and the condition ADR-0021 set for revisiting
("after the light system is stable") passed long ago.

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

### Moneydance import

*[moneydance-import-fidelity.md](moneydance-import-fidelity.md) holds the full ranked gap
list; the two entries below are the ones carrying an open obligation. Both are
constrained the same way: ADR-0052 D2 makes the importer seed-once, so adding a column
later is not enough — each needs a PAIRED BACKFILL over the retained raw payload, in the
same slice.*

*A third entry lived here until 2026-08-28: the collapse of per-leg tags onto the
header. It moved to **Next › 3 · Splits › Per-posting tags**, because it is not really an
importer defect — the importer collapses them because there is nowhere to put them, and
the same backfill-pairing rule applies once `txn_leg_tags` exists.*

#### `ol.orig-payee` / `ol.orig-memo` are dropped on import

*open. Ranked #2 by impact in the fidelity doc.*

Neither `bank_payee` nor `bank_memo` exists anywhere in `db/migrations` or `src`.
Moneydance keeps both the user's cleaned payee and the bank's original string; Coffer
keeps one, so years of payee cleanup arrive flattened and the original is unrecoverable
from the ledger. Columns plus backfill, together.

#### The `isMultiSplit` guard discards single-leg `0.desc`

*open. The fidelity doc calls the fix trivial; it is still there.*

[TransactionMapper.cs:183-196](../src/Importer.Moneydance/Mappers/TransactionMapper.cs#L183-L196)
computes `var isMultiSplit = emittable.Count > 1;` and keeps the leg memo only when that
holds — duplicated in `ReminderMapper.cs`. The doc quantifies the loss: a couple thousand
single-leg events where `txn.desc` is "CONTRIBUTION" and the discarded `0.desc` held the
fund name, which is exactly the leg-level context an investment register exists to show.

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

#### Drop the vestigial counters on `sync_runs`

*Status: open cleanup. Slice 2c.1 left `txns_merged` / `txns_queued` /
`txns_skipped` in place on `sync_runs` (always 0 — they're
from the pre-2c merge / staging pipeline). Migration 044
dropped the sibling vestigial tables (`pending_transactions`,
`merge_candidates`, `merge_rules`, `transaction_rules`); the
sync_runs counters survived because they live on a table we
still use. Drop them in a future cleanup.*

#### Sync activity log retention

*Status: open, and the condition it named has now been met. Slice 2c.1 keeps every
`sync_runs` row forever. At one sync/day that's ~365 rows/year/connection — fine
through the personal-use horizon, and this item said to re-evaluate "when daily polling
lands". It landed: scheduled feed sync shipped in v0.68.0 (`FeedSyncJobHandler` +
migration 215), so rows now accrue whether or not anyone clicks anything. Likely shape:
keep last 90 days verbose, roll older runs into a per-month summary row; or simply LIMIT
the list query and never paginate beyond N. No automation today.*

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
on/off toggle) and apply it on the orchestrator path
(`SimpleFinPullProvider` -> `IngestOrchestrator`) before the leg
insert — `SimpleFinSyncService`, which this item used to name, was
deleted by ADR-0031 Phase 2. Approve flow stays unchanged.*

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

#### `recentPrices[].source` is hard-coded NULL on a premise that expired

*open cleanup. Surfaced 2026-08-28.*

[SecuritiesRepository.cs:127-133](../src/Api/Db/Repositories/SecuritiesRepository.cs#L127-L133)
projects `new SecurityPricePointDto(p.PriceDate, p.Price, (string?)null)` behind a comment
stating that "the `security_prices` table doesn't track a free-form source label today".
It does: migration 130 added `security_prices.source NOT NULL` (ADR-0054 D2), widened by
migrations 154 and 177, and the SPA already renders it for the richer prices table via
`priceSourceLabel(p.source)`
([SecurityDetailPage.tsx:649](../src/Web/src/routes/ledgers/SecurityDetailPage.tsx#L649)).

Only this contract still hands out null, while its own DTO documents the field as a
"Free-form source label" and the SPA types it `string | null`. Every consumer of the
security-detail endpoint, MCP clients included, is left unable to tell a manual price
from a fetched or trade-derived one — from a row that records exactly that. One-line
projection change; the comment is the real defect, since it reads as current fact.

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

### Documentation drift

*Found 2026-08-28 by a sweep for work recorded outside this file. Grouped because the
fix is the same in each case — the code moved and the prose did not — and because prose
drift is the failure mode that costs a reader an hour and produces no error message.*

#### The ADR index contradicts the ADRs it indexes

[decisions/README.md](decisions/README.md) line 69 lists **0063** as *Proposed*; that
ADR's own header reads "Accepted (v1 = v0.5.0, built + validated end-to-end)" and the MCP
surface has been live for a dozen minor versions. Line 98 lists **0092** as *Proposed*,
while 0094 and 0095 — both *Accepted* — are recorded in the same table as amending it.
Two shipped, security-relevant decisions read as speculation at the entry point a
maintainer scans first.

#### `glossary.md` and `architecture.md` §6.6 describe the pre-ADR-0022 data model

The glossary's **Counterparty**, **Double-entry / symmetric postings**, **Override**,
**Pending transaction** and **Resolved view** entries are each written around a
`transactions` table paired by `counterparty_id`, with edits in `transaction_overrides`
and unsettled rows in `pending_transactions`. Migration 025 dropped the first three of
those tables and migration 044 the rest; the model has been headers + legs since
ADR-0022, which superseded the ADR-0019 those entries still cite as current, and
`AppDbContext` maps no `transactions` table at all. `architecture.md` compounds it by
describing `SimpleFinSyncService` as the live sync orchestrator after ADR-0031 Phase 2
replaced it. A newcomer reading the glossary is reading a schema that no longer exists.

#### Migration 113's header cites a share scale the schema had already dropped

[113_txn_headers_ingest_investment_fields.sql:23](../db/migrations/113_txn_headers_ingest_investment_fields.sql#L23)
documents shares as `NUMERIC(28,8) per holdings.quantity`. Migration 043 — seventy
migrations earlier — set that column to `NUMERIC(25,12)`. Cosmetic in isolation, not
cosmetic as a pattern: migrations 205 and 209 each rounded money at a scale a stale
header asserted, and the second one put three disposals a cent out on a real ledger. A
migration header is load-bearing documentation here and wants the same scrutiny as the
DDL beneath it.

**Decided 2026-08-28: mechanise it, and fix the doc rather than the migration.**
`engineering-standards.md` §3.1 — "once a migration file is committed to `main`, it is
never edited" — forbids correcting 113 in place, so the fix is the comment-side twin of
the guard that already exists for columns (`SchemaDriftGuardTests`): parse `NUMERIC(p,s)`
claims out of migration comments and compare them against `information_schema`, with 113
recorded in the guard's allow-list as known-stale so the check does not start permanently
red. `database-schema.md` carries the correction a reader will actually find. Rejected:
a corrective `COMMENT ON COLUMN` migration, which fixes one instance and catches none.

#### Two xmldoc `cref`s point at a type ADR-0031 deleted

`SimpleFinClient.cs` carries `<see cref="SimpleFinSyncService"/>` twice (lines 24 and
188) and no such class exists anywhere in `src/` — Phase 2 replaced it with
`SimpleFinPullProvider` over `IngestOrchestrator`. Four other files reference it in prose
comments, two of them correctly flagged as pre-ADR-0031 history and two not.

**Decided 2026-08-28: fix the three by hand now; the compiler flag waits.** Turning on
`GenerateDocumentationFile` would catch this class automatically, but `Api.csproj`
already sets `TreatWarningsAsErrors=true`, so switching it on surfaces 36 CS1574 sites
across 31 distinct cref targets — plus 4 CS1584, 140 CS1573 and 238 CS1587 — all of them
blocking. That is its own PR, on its own schedule.

#### `GenerateDocumentationFile` is off, so no cref is checked

*parked, with the condition stated. Split out of the entry above on 2026-08-28.*

36 blocking sites is the whole cost, and it is a one-time cost: `SimpleFinSyncService` is
2 of them, and the rest include `LegDerivedRecomputeService`, `SnapshotScheduler`,
`LedgerSnapshotSerializer`, `LedgerSnapshotRestorer`, `HoldingsRecomputeInterceptor`,
`BootstrapTokenService`, `PostingIndex` and `LegSpecKey`. Reopen when someone has an
afternoon for it — after which every future dangling cref fails the build instead of
being found by a sweep.

#### `architecture.md` files tax-lot selection under Non-goals

It is queued work — see "A5 — Edit Lots affordance" above, which is exactly this feature.
The parenthetical marks it deferred, but sitting under a **Non-goals** heading it reads
as a decision not to build it.

---

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

#### The consistency monitor runs a full check 96 times a day to announce once
*open. Surfaced 2026-09-01 while explaining the cadence after the 0.71.0 deploy. The
set-based shape the fix needs already exists eighty lines up in the same file.*

`ConsistencyMonitor.CheckAllAsync` runs on **every** scheduler tick
([SchedulerService.cs:127](../src/Api/Scheduling/SchedulerService.cs#L127)), which is
[15 minutes](../src/Api/Scheduling/SchedulerService.cs#L25). The monitors are
deliberately not `global_scheduled_jobs` rows — nobody should be able to switch off
the thing whose whole purpose is noticing that the configurable jobs stopped — and
that part is right.

What is not is that **only the publish is throttled**.
[`RepeatAfter` is 24h](../src/Api/Notifications/ConsistencyMonitor.cs#L36), so a
ledger is announced at most once a day, while the full `CheckAsync` behind it runs 96
times a day. On a real ledger measured 2026-09-01 that is 103,213 balance rows, 179
positions and 42,919 posting counts re-verified every fifteen minutes to send at most
one notification.

**Realized gains is N+1; its neighbour checking the same rows is not.**
[`CheckRealizedGainsAsync`](../src/Api/Db/Repositories/LedgerConsistencyRepository.cs#L170)
loops over positions and awaits TWO queries *inside* the loop
([:181](../src/Api/Db/Repositories/LedgerConsistencyRepository.cs#L181)) — the stored
rows, then `realized_gains_walk(account, security)`, which replays FIFO lot
consumption for that pair. 179 positions is 358 sequential round-trips per pass, ~34k
a day.
[`CheckHoldingsAsync`](../src/Api/Db/Repositories/LedgerConsistencyRepository.cs#L90),
eighty lines above it, checks the SAME 179 positions in TWO queries: the stored rows,
one whole-ledger `holdings_cost_basis_as_of` walk, then a comparison in memory. The
set-based shape is already proven in this file, against this data, by the projection
next door.

Migration 217 raised the per-pass cost on purpose: the comparison went from one
`SUM(realized_gain)` per position to six columns compared per disposal row. That was
the right trade for correctness — the old shape could not see the long-term split at
all — but it makes the frequency question worth answering rather than assuming.

**Measure first, and measure the right thing.** The temptation is to time
`CheckAsync` end to end and call it slow or fine. What decides between the two fixes
below is the SPLIT: how much of a pass is the 358 round-trips versus the two
whole-ledger walks. `Integration.Stress` already seeds a 50k-transaction ledger and is
excluded from the sharded suite, so it is the place to put a per-projection
breakdown.

**Two levers, and they are not alternatives.**

1. **Make the check cheap.** Give `realized_gains_walk` a whole-ledger overload the
   way `holdings_cost_basis_as_of` has one, and compare in memory. This is the one to
   do first: it helps the on-demand button and the scheduled pass equally, it removes
   an N+1 that grows with the portfolio, and it needs no decision about detection
   latency.
2. **Throttle the check to the publish window.** Straightforward, but it trades away
   early detection for CPU, and it should not be done blind — if lever 1 makes a pass
   cheap enough, this buys little and costs the one thing the monitor exists for.

**Do not touch the Maintenance button.** It is the on-demand path, and someone
watching it has asked for exactly this work; any throttle belongs on the tick, not on
the request.

Worth knowing while measuring: the first tick runs at process start with **no** initial
delay (`ExecuteAsync` calls `TickAsync` before its first `Task.Delay`), so every
restart pays a full check immediately. That is a feature on deploy — it is how the
0.71.0 upgrade surfaced a cent of `cost_basis_sold` drift within seconds of coming up
— and a cost on a container that is crash-looping.

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


#### `monthly_account_balances` -- measured, and not needed

*parked with a number, 2026-08-29. Reopen if the figures below change by an order of
magnitude, or if a caller starts asking for daily points.*

ADR-0008 deferred the materialized view because "the view exists to serve a feature
(net-worth charts) that doesn't exist yet". That feature shipped and had to be capped at
600 points, which read like evidence the view was overdue. Nobody had measured it, so the
deferral kept being re-argued from the same unmeasured premise.

Measured now by `Stress/NetWorthHistoryCostTests`, on the `Default` stress ledger -- 50k
transactions, ~24 years of horizon, 200 securities:

| series | points | wall | per point |
|---|---|---|---|
| monthly, full horizon | 289 | 1.2 s | 4.1 ms |
| yearly, full horizon | 25 | 0.2 s | 8.8 ms |

**So: do not build it.** The full monthly series a chart would request costs about a
second, and the 600-point ceiling implies roughly 2.5 s at the very worst. A materialized
view would add a refresh path, a staleness question, an RLS problem (a matview cannot
carry RLS and is populated by its owner, so exposing it to `coffer_app` needs a wrapping
`security_invoker` view) and a new drift surface owing a consistency projection -- to
optimise something that is not slow.

Note the per-point figures run the *wrong* way: the yearly series costs more per point
than the monthly one, because fixed setup dominates when there are only 25 points. Any
future argument from "cost per point" should account for that; total wall time is the
number that matters.

