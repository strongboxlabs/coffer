# 0046 — Denormalized posting counts + register read-path performance

* Status: Accepted
* Date: 2026-06-10
* Refines: [ADR-0036](0036-originating-vs-target-register-entries.md)
* Follows: [ADR-0032](0032-triggers-as-last-resort.md), [ADR-0034](0034-header-walk-running-balance.md)
* Migration: 120

## Context

`resolved_transactions` computed ADR-0036's originating-vs-target
discriminators — `account_postings_on_header` and
`header_total_postings` — with **two correlated
`COUNT(DISTINCT posting_index)` subqueries that ran once per row**.

Measured against real data (a 15.7K-leg account):

| Query | cost of the two subqueries |
|---|---|
| Windowed register page (~100 entries) | ~10–15 ms (negligible) |
| **Full-account scan** (reports / aggregation) | **~110 ms** (dominant per-row cost) |

The windowed register hides this — but a report that aggregates over a
whole account, or several accounts in a group, scans every row and pays
it in full. The subqueries also inflate the view's planner cost, which
is part of what trips Postgres JIT compilation on this view (the
`resolved_transactions` join graph already compiles ~250 functions/query
— the reason PR #45 shipped the `ALTER ROLE coffer_app SET jit = off`
stopgap, see [follow-ups](../follow-ups.md) and
`db/init/00-init-roles.sh`).

**Principle (the deciding one): optimize each layer independently.** The
data layer must be fast *by design* — not because the UI happens to
window, and not because JIT is disabled at the connection. A view that's
only cheap on the convenient access pattern is a latent regression for
the report workload.

## Decision

1. **Denormalize both counts onto `txn_legs`** (mig 120). The view reads
   two columns instead of running the correlated subqueries — `O(rows
   returned)` for *every* workload (page and full scan alike). A
   view-rewrite-via-derived-table alternative was measured and rejected:
   it aggregates the whole `txn_legs` table regardless of filter, trading
   page latency for scan latency. Only denormalized columns are fast for
   both.

2. **Maintain via explicit recompute, NOT a trigger.** Per ADR-0032/0034
   the project deliberately removed the data-maintenance trigger family
   in favour of call-site recompute. Posting counts derive from the same
   `txn_legs` structural changes the balance interceptor already
   snapshots (it computes the distinct affected header ids), so
   posting-count recompute is folded into that **same service +
   interceptor** — renamed `BalanceRecomputeService` /
   `BalanceRecomputeInterceptor` → `LegDerivedRecomputeService` /
   `LegDerivedRecomputeInterceptor` to reflect the broadened role. One
   snapshot, one path; `fn_recompute_posting_counts_for_header(uuid)` +
   its TVF wrapper mirror `recompute_balances_for_account`. Holdings
   stays its own service (FIFO lots, a different concern).

3. **Retire the `insert_investment_legs` TVF → EF-tracked inserts.** That
   function existed only to batch leg inserts so the (now-removed)
   per-statement `txn_legs` triggers fired once; with the triggers gone
   its reason to exist went too. It executed a server-side INSERT via a
   `FromExpression` TVF, bypassing the EF ChangeTracker — which forced
   `InvestmentTransactionsRepository.Create/PatchAsync` to drive every
   recompute by hand. Switching to tracked `AddRange` lets both
   interceptors fire automatically, removing all explicit recompute calls
   there (balances, holdings, **and** posting counts). The Dapper importer
   remains the lone ChangeTracker-bypassing writer that recomputes
   explicitly.

4. **FK indexes** (hygiene, unrelated to the counts) on
   `accounts.parent_id` / `holdings_account_id` / `feed_connection_id`
   and `recurring_transactions.source_account_id` — partial (`WHERE … IS
   NOT NULL`).

## Results (real-data, `coffer_app` = the app's JIT-off path)

| Query | before | after |
|---|---|---|
| Full-account scan | ~362 ms | **~240 ms** (subquery cost removed) |
| Register page | ~64 ms | **~49 ms** |

Backfill verified exact (0 legs whose stored counts differ from a fresh
recompute). Secondary effect: the lighter view's planner cost no longer
trips JIT even with JIT *on* **on the windowed page** (a page dropped
141 → 62 ms measured as a JIT-enabled superuser, with the `JIT:` plan
node gone). The heavier full-account scan still trips JIT — see the
close-out below for why that means `jit = off` stays rather than being
retired.

## Close-out — the `jit = off` question is settled (keep it)

This ADR originally framed two follow-on steps toward "retiring the
`jit = off` stopgap." Both were measured and **closed without lifting
it** — `jit = off` is the deliberate, measured optimum for this OLTP
workload, not debt:

* **Counterparty self-`LEFT JOIN` + `account_path()` — won't denormalize.**
  Profiled cheap: `account_path()` is `STABLE` (cached per statement) and
  there are few distinct counterparties, so the self-join is ~2 ms over
  the base scan. Not the lever; a maintained column + cross-header
  recompute set for ~2 ms isn't worth the surface.
* **Final JIT re-measure (real data, ~16K-leg account, `coffer_app`, RLS
  on).** With every correlated subquery gone, the *windowed page* no
  longer trips JIT (so the role setting is a no-op there: ~102 ms off /
  ~118 ms on). But the *full-account / report scan* still trips JIT (282
  functions, ~70-100 ms compile) with **no execution benefit** — ~1653 ms
  off vs ~1755 ms on. Lifting `jit = off` would only regress reports.

So `jit = off` stays, reclassified from interim stopgap to deliberate
configuration. Rationale lives at the `ALTER ROLE` line in
`db/init/00-init-roles.sh`; the follow-up
(`docs/follow-ups.md` "View join cost + role-level JIT-off workaround")
is marked RESOLVED with the measurement table.

## Consequences

* `txn_legs` carries two maintained denormalized columns (NOT NULL
  DEFAULT 1; the interceptor corrects multi-posting headers post-commit,
  exactly like balances). A drift would surface the same way a balance
  drift would — via a recompute-and-diff health check.
* One write path (investment Create/Patch) changed from a bespoke TVF to
  the standard EF insert every other writer uses — less surface, no
  ChangeTracker-bypass exception to remember.
* The recompute service/interceptor now own two leg-derived
  denormalizations; a third (counterparty) would slot in the same way.
