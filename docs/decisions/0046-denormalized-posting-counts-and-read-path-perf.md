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

The cost is paid once per row returned:

| Query | cost of the two subqueries |
|---|---|
| Windowed register page (~100 entries) | a page's worth of rows (negligible) |
| **Full-account scan** (reports / aggregation) | **every row of the account** (the dominant per-row cost) |

The windowed register hides this — but a report that aggregates over a
whole account, or several accounts in a group, scans every row and pays
it in full. The subqueries also inflate the view's planner cost, which
is part of what trips Postgres JIT compilation on this view (the
`resolved_transactions` join graph already compiles a large number of
functions per query — the reason `ALTER ROLE coffer_app SET jit = off` exists, see the open-work backlog and
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

## Results (`coffer_app` = the app's JIT-off path)

Neither query runs the correlated subqueries any more: the register page
stops paying them for a page's worth of rows, and the full-account scan stops
paying them for every row of the account's history.

The backfill (mig 120, step 3) counts `DISTINCT posting_index` per header and
per (header, account) — the same two counts
`fn_recompute_posting_counts_for_header` derives — so backfilled and recomputed
values agree by construction.
Secondary effect: removing the subqueries lowers the view's planner cost. The
windowed page's cost is bounded by its page size, so it falls below
`jit_above_cost` and does not trip JIT even with JIT *on*. The full-account
scan's cost grows with the account's history, so it can still trip JIT — see
the close-out below for why that means `jit = off` stays rather than being
retired.

## Close-out — the `jit = off` question is settled (keep it)

Neither of the two follow-on steps toward "retiring the `jit = off` stopgap"
**lifts it** — `jit = off` is the deliberate optimum for this OLTP workload, not
debt:

* **Counterparty self-`LEFT JOIN` + `account_path()` — won't denormalize.**
  Cheap, and the reason is structural: per row, the self-join is
  one probe of the `txn_legs (header_id, posting_index)` index for the
  sibling leg, and `account_path()` is a `STABLE` walk up the account's
  parent chain, bounded by tree depth rather than ledger size — so it adds
  little over the base scan and does not grow with it. A maintained column
  would cost more than it saves: the counterparty name changes whenever that
  account or any ancestor of it is renamed or re-parented, so one rename
  would fan out to every leg whose sibling sits anywhere beneath it, and
  every leg write would have to refresh its siblings on the same header.
  That is a new invalidation surface owing its own drift check, to remove a
  cost that does not scale with the data.
* **JIT with the subqueries gone (`coffer_app`, RLS on).** With every correlated
  subquery gone, the *windowed page* no longer trips JIT (so the role
  setting is a no-op there). But the *full-account / report scan* can
  still trip JIT, and gains **no execution benefit** from it — it runs no
  faster with JIT on than off, so the compile is pure cost.
  Lifting `jit = off` would only regress reports.

So `jit = off` stays, reclassified from interim stopgap to deliberate
configuration. The rationale lives at the `ALTER ROLE`
line in `db/init/00-init-roles.sh`. The follow-up entry that tracked this was
resolved and deleted, per that file's lifecycle.

## Consequences

* `txn_legs` carries two maintained denormalized columns (NOT NULL
  DEFAULT 1; the interceptor corrects multi-posting headers post-commit,
  exactly like balances). *(Amended 2026-10-27, [ADR-0108](0108-memo-and-tags-per-side.md) D6: after the save's
  statements, inside the save's transaction; the correction commits or rolls
  back with the save, as the balances do, ADR-0034's amendment of that date.)* A drift would surface the same way a balance
  drift would — via a recompute-and-diff health check.
* One write path (investment Create/Patch) changed from a bespoke TVF to
  the standard EF insert every other writer uses — less surface, no
  ChangeTracker-bypass exception to remember.
* The recompute service/interceptor now own two leg-derived
  denormalizations; a third (counterparty) would slot in the same way.
