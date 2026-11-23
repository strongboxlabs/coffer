# 0032 — Triggers as a last resort; validation invariants live in API code

* Status: Accepted (principle locked; per-trigger removal slices tracked
  in the open-work backlog)
* Date: 2026-05-29
* Related: ADR-0019 (symmetric postings), ADR-0022 (txn_headers and legs),
  ADR-0029 (investment transaction editor)

## Context

The schema accreted **18 triggers** on the transaction tables across
migrations 030 / 056 / 057 / 061 / 062 / 068 / 069. The triggers
fall into two families:

* **Validation triggers** (4) — enforce coherence rules between
  `txn_headers` and `txn_legs` (`posting_role` IS NOT NULL ⇔
  `action` IS NOT NULL; symmetric-posting cardinality; sum-of-
  postings = 0; user-ledger-grants owner-present).
* **Recompute triggers** (14, including the per-event INSERT /
  UPDATE / DELETE variants) — maintain derived state
  (`txn_legs.balance_after` running totals; per-(brokerage,
  security) holdings rows + per-lot cost basis).

The validation triggers were added defensively: each enforced an
invariant the API code *also* enforced, on the theory that
"trigger + code" is safer than "code alone." The theory did not
hold:

### The chain-of-triggers failure

A PATCH that upgrades a feed-imported brokerage row from bank-shape
to BuyXfr fails with:

> P0001: posting_role required on legs of investment headers
> (header_id=…, action=buyx, account_id=…, posting_index=0)

The "violation" is transient. The chain that produces it:

1. EF flushes `UPDATE txn_headers SET action='buyx'` (the header
   transitions from null → buyx).
2. AFTER UPDATE on `txn_headers` fires
   `trg_headers_balance_after_update` → CTE + `UPDATE txn_legs SET
   balance_after = …` on every leg of the header (recompute,
   touches no other columns).
3. BEFORE UPDATE on each leg fires `trg_validate_posting_role`.
   It reads `header.action='buyx'` (just changed) and
   `leg.posting_role=NULL` (still the old bank-shape value;
   pending DELETE later in the same EF batch).
4. Trigger raises. Rollback. User sees a generic error.

The validation is correct *in isolation* — a buyx leg without a
posting_role IS a violation. But the validation fires during a
multi-statement update where the API is mid-conversion. The
next statement in the same batch (the DELETE + new INSERT with
proper roles) resolves the brief inconsistency. Chained triggers don't have visibility into "the parent
operation is mid-flush"; they evaluate each statement in
isolation.

### What we lose by leaning on triggers

* **Diagnostic clarity.** When a trigger raises, the user-facing
  error refers to a row state they didn't create. Debugging
  requires reading three triggers, the EF batch order, and the
  PL/pgSQL stack.
* **Refactor friction.** Every change to the API's write path
  must be audited against every trigger that might fire as a
  side-effect. Adding a column to a recompute means re-auditing
  all consumers of every leg writer.
* **Test cost.** Trigger-specific tests (e.g.
  `Trigger_rejects_NULL_posting_role_on_investment_header_leg`)
  exercise raw SQL inserts to provoke the trigger. They give us
  no confidence the API path enforces the rule, because they
  bypass it entirely.

### What we'd lose by removing triggers entirely

* **Defense in depth.** A future contributor adding a new write
  path could forget to enforce the invariant; the trigger would
  catch them.
* **Direct-SQL safety.** Data fixes via `psql` would no longer
  trip the rule; the operator must hold it themselves.

## Decision

### 1. Triggers are a last resort, not a first defense

A new trigger may be added ONLY if:

1. **The invariant is end-to-end DB-owned** — no API path can
   reasonably enforce it, OR the side-effect (recompute,
   cascading update) cannot be reproduced at the call site
   without duplication.
2. **The trigger is idempotent** — running twice on the same
   state produces the same result, so chained-trigger interleaving
   doesn't cause transient false positives.
3. **The trigger doesn't read other tables that another trigger
   in the same statement might mutate.** Chains of validation
   triggers reading mutating state are the bug pattern this ADR
   exists to prevent.

If those conditions aren't met, the invariant lives in API code.

### 2. Validation invariants MUST live in API code

Every coherence rule between rows / tables is enforced by the
repository or endpoint that writes the rows. Specifically:

* `InvestmentTransactionsRepository.CreateAsync` / `.PatchAsync`
  builds legs via `InvestmentPostings.BuildPostings` /
  `.BuildHoldingsImpact`, which is the canonical source of valid
  `posting_role` / cardinality / completeness.
* `IngestOrchestrator` writes bank-shape rows with
  `action=NULL + posting_role=NULL` (upholds the invariant from
  the other side).
* `Importer.Moneydance.Db.TransactionsRepository` stamps role on
  every leg insert; the one-time backfill in migration 057's
  Parts 1–2 cleaned historical data once and for all.

Tests **prove the API path enforces the rule**, not that the
trigger does. Integration tests assert post-state on the
resolved view (e.g. `Assert.Equal("security", leg.PostingRole)`).

### 3. Recompute triggers: analyze case-by-case

Recompute triggers maintain *derived* state — they don't enforce
invariants, they compute values. Moving them to API code means
every writer calls a recompute helper. Trade-off:

| Trigger | Discipline cost if moved | Chain risk | Decision |
|---|---|---|---|
| `trg_headers_balance_after_update` | High (every header date change) | Medium (kicks off leg UPDATEs) | **Removed mig 102** — moved to `LegDerivedRecomputeInterceptor` per ADR-0034 §"Why an interceptor and not a trigger" |
| `trg_legs_balance_after_*` (3) | High (every leg write) | Low (no other trigger reads balance_after) | **Removed mig 102** — same |
| `trg_header_overrides_recompute_*` (3, mig 099) | High | Medium | **Removed mig 102** — same |
| `trg_leg_overrides_recompute_*` (3, mig 101) | High | Medium | **Removed mig 102** — same |
| `trg_txn_legs_recompute_*` (4, holdings/lots) | Very high (holdings + lots, multiple tables) | Low (idempotent recompute) | **Removed mig 104** — moved to `HoldingsRecomputeInterceptor` (sibling of `LegDerivedRecomputeInterceptor`); call-site recompute for the `insert_investment_legs` TVF path (`InvestmentTransactionsRepository.Create/PatchAsync`) and importer Dapper path |
| `trg_accounts_recompute_on_commission_flip` | Small (a thin `RETURNS TABLE(recomputed_count INTEGER)` SQL wrapper bound via `HasDbFunction` lets the API invoke the recompute via LINQ — same pattern as `insert_investment_legs`) | Low | **Removed mig 088** — `AccountsRepository.SetIsTradeCommissionAsync` calls the recompute explicitly after flipping the flag |

**Balance triggers retired in mig 102.** The family has four
distinct failure modes under EF's batched `SaveChanges`
(cascade-from-header DELETE order, override-on-posted_at bypass,
override-on-amount bypass, and merge-with-reshape batch fire order). The structural fix:
move the recompute to API call sites via a `SaveChangesInterceptor`
in C# (`LegDerivedRecomputeInterceptor`) that scans `ChangeTracker` and
invokes the recompute SQL function (`fn_recompute_balances_for_account`)
once per save. Bulk paths that bypass the ChangeTracker
(`ExecuteUpdateAsync` / `ExecuteDeleteAsync`, Dapper, raw SQL) invoke
`LegDerivedRecomputeService` explicitly. See
[ADR-0034](0034-header-walk-running-balance.md) for the full
rationale and the interceptor design.

**Holdings/lots recompute triggers retired in mig 104.** Their surface
is different and their trigger set narrower, but the balance triggers'
failure modes apply to them in latent form — AFTER STATEMENT triggers see per-statement transition
tables (not post-SaveChanges end state), the recompute is invisible
to the writer, the dispatch logic duplicates what the interceptor
pattern gets for free. The structural fix: a `HoldingsRecomputeInterceptor`
parallel to the balance one. It scans the `ChangeTracker` for
investment-shape `TxnLegRow` entries (`security_id IS NOT NULL` and
`quantity IS NOT NULL`), captures BOTH the OLD and NEW `(account, security)`
pairs on Modified entries so legs moving between holdings reconcile
both ends, and invokes `HoldingsRecomputeService` (which calls
`recompute_holdings_cost_basis` via a new TVF wrapper) after the
save. The `insert_investment_legs(jsonb)` TVF path bypasses the
ChangeTracker (raw SQL), so `InvestmentTransactionsRepository.Create/PatchAsync`
calls both `LegDerivedRecomputeService` and `HoldingsRecomputeService`
explicitly after each TVF insert — same #4 call-site pattern as
`BulkTransactionsRepository`. The importer's end-of-import
`RecomputeCostBasisAsync(ledgerId)` (Dapper) is unchanged.

> **Update (mig 120):** `insert_investment_legs` has since been retired.
> It only ever existed to batch leg inserts into one statement so the
> per-statement txn_legs triggers fired once; with that trigger family
> gone (this ADR), the TVF's reason to exist went too. The investment
> editor's Create/Patch now insert legs as EF-tracked rows, so the two
> interceptors cover both recomputes automatically — no explicit
> recompute call remains in `InvestmentTransactionsRepository`. The
> importer's Dapper path is still the lone ChangeTracker-bypassing
> writer that calls the recompute services directly.

The commission-flip trigger was different: a single writer
(`SetIsTradeCommissionAsync`), a single downstream effect
(recompute one brokerage's holdings), and the data flow was
hidden behind an AFTER UPDATE side-effect. Moving it to an
explicit call site made the data flow visible without any
discipline cost — a 10-line wrapper + LINQ call replaced the
trigger cleanly.

**Rule of thumb:** a recompute trigger that exists because one
call site is the only writer should move to that call site.
A recompute trigger that exists to cover N writers should stay.

### 4. Per-trigger removal slices

Removal happens **one trigger per slice**, each with:

1. **Audit** — every write site that targets the affected table;
   confirm every site upholds the invariant.
2. **Migration** — `DROP TRIGGER … ; DROP FUNCTION …`.
3. **Test housekeeping** — remove trigger-specific tests
   (`Trigger_rejects_*`); replace with an API-level assertion if
   none exists.
4. **Commit message documents the API surface that now owns the
   invariant.**

Removal order — all five shipped, so the list below is the record; the backlog
entry that tracked it was resolved and deleted:

1. ~~`trg_validate_posting_role`~~ — done in migration 084.
2. ~~`trg_validate_posting_cardinality_insert` / `_update`~~ — done in migration 085.
3. ~~`trg_validate_posting_completeness`~~ — done in migration 086.
4. ~~`trg_user_ledger_grants_owner_present`~~ — done in migration 087.
5. ~~`trg_accounts_recompute_on_commission_flip`~~ — done in
   migration 088. Moving it needs no raw SQL in the repository:
   `HasDbFunction` is precisely the documented
   escape hatch (engineering-standards §4.2.1) — a thin scalar
   wrapper (`recompute_holdings_for_brokerage`) bound to a
   `RETURNS TABLE(recomputed_count INTEGER)` SQL function lets
   the API invoke the recompute via LINQ, same pattern as
   `insert_investment_legs` (migration 069). The data flow is
   now visible at the call site:
   `SetIsTradeCommissionAsync` flips the flag, then explicitly
   refreshes derived state.
6. ~~**Stop** — keep all remaining recompute triggers
   (`trg_*_balance_after_*`, `trg_txn_legs_recompute_*`).~~
   Superseded by §3: both families are gone (migrations 102 and
   104), replaced by the two `SaveChangesInterceptor`s, which run
   the recompute for every tracked leg writer without a helper call
   at each site.

## Consequences

### Better

* Diagnostic errors point at the user's action, not at a chain
  of side-effects.
* New API write paths are easier to audit — the integrity
  contract is in code next to the writer.
* Refactors that touch leg writes don't require re-verifying
  unrelated triggers.
* Tests prove API behavior, not trigger behavior. API behavior
  is what users observe.

### Worse

* A future contributor who adds a write path without going
  through the existing repositories could break the invariant
  silently.
* Direct-SQL data fixes have no safety net for these
  invariants — operators must hold them.

### Mitigations

* Code review checklist (engineering-standards §3.3) prohibits
  raw `INSERT INTO txn_legs` outside repository code.
* Integration tests cover every write-path action × shape
  combination at the API surface.
* The repository layer is small (one investment repository,
  one orchestrator, one importer) — the surface to audit is
  bounded.

## Out of scope (parked)

* Replacing recompute triggers with materialized views or
  trigger-free recompute jobs.
* Per-row policy enforcement (RLS) — orthogonal to this ADR;
  enforced separately by Postgres role-based security.

## Amendment, 2026-11-03 — a header change counts for every line it has

§3 describes `HoldingsRecomputeInterceptor` as scanning the ChangeTracker for
investment-shape `TxnLegRow` entries. The walk it maintains reads the header too: it
leaves out a hidden or merged header, orders by the header's date, and reads its
action. So a save that changed only those, with no line written, recomputed no
position: deleting an imported row (which hides it), restoring one row, and the date a
merge moves. `LegDerivedRecomputeInterceptor` already counted such a header for the
balances.

The rule, for every interceptor that maintains a projection from the lines: a modified
`TxnHeaderRow` whose change touches a header column the projection's recompute reads
counts as a change to every line of that header, so the projection is recomputed for
each of them in the same save:

| Projection | Interceptor | Header columns that count |
|---|---|---|
| Balances, posting counts | `LegDerivedRecomputeInterceptor` | `posted_at`, `is_merged_into`, `is_hidden` |
| Holdings, lots, realized gains | `HoldingsRecomputeInterceptor` | `posted_at`, `is_hidden`, `is_merged_into`, `action` |
| Trade prices | `TradePriceFromLegInterceptor` | `posted_at`, `action`, `provider_raw_payload`, `is_hidden`, `is_merged_into` |
| Stored memo-and-tags presentation | `LegNotesRecomputeInterceptor` | `memo`, `action`, `originating_account_id` |

The first three name their columns in `RecomputeHeaderProperties`, and
`HeaderRecomputePropertyCatalogTests` holds each set to the SQL its recompute calls:
every header column that SQL names and that can change after insert is in the set, and
nothing else is. The presentation is computed in C# (`LegNotes`) and its interceptor
has always counted those three header columns. The holdings and trade-price
interceptors leave a recurring template's header out, as they leave out its lines
(ADR-0047).

Writers that bypass the ChangeTracker still recompute explicitly; the bulk delete and
bulk restore already did, as one batch over the selection. The trade-price interceptor
re-derives the day a moved trade now sits on and the day it left, which takes the price
of the executions that remain or, with none, keeps its row; the bulk delete and
`merge_securities` re-derive the days of the executions they remove or move (ADR-0084
D4, 2026-11-03 amendment). Since migration 273 a hidden or merged-away row is no trade
(ADR-0084 D4, 2026-11-04 amendment), so the trade-price set gained `is_hidden` and
`is_merged_into`, and the bulk delete also re-derives the days of the rows it hides, the
bulk restore those of the rows it unhides.

**A deleted merge winner un-merges its losers.** `is_merged_into` is `ON DELETE SET
NULL` (migration 121), so deleting a winner, a manual row being a valid one, puts each
loser back in the register and in both walks, on every account it has a line on, the
category line of a feed row included. The database does that past the ChangeTracker.
Every tracked removal of a header (the bank and investment single deletes, and the
in-kind conversion that deletes its sale and purchase) first loads the losers tracked
(`MergeLosers.TrackAsync`), so EF's own fixup of the relationship
(`DeleteBehavior.SetNull`) marks each one's `is_merged_into` modified, and the rule
above recomputes its balances and positions in the same save, and, since migration 273,
re-derives the days of its trades; a guard test holds every
`TxnHeaders.Remove` to it. The bulk delete reads the losers' lines before its
`ExecuteDeleteAsync` and adds them to its own balance and holdings recomputes, and,
since migration 273, the days of their trades to its trade-price re-derivation: an
un-merged row is a trade again.
