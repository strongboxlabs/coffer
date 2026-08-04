# 0008 — Defer `monthly_account_balances` materialized view to Phase 8

* Status: Accepted
* Date: 2026-05-08

## Context

[architecture.md](../architecture.md) §6.5 sketches a materialized view that pre-aggregates per-account closing balances by month, so a 10-year net-worth chart hits 120 rows per account regardless of underlying transaction count.

The draft SQL provided in the architecture sketch does not compile in PostgreSQL. It mixes an aggregate with a window function inside the aggregate's `FILTER` clause:

```sql
MAX(balance_after) FILTER (
    WHERE posted_at = MAX(posted_at) OVER
        (PARTITION BY account_id, date_trunc('month', posted_at))
)
```

PostgreSQL rejects this — aggregates cannot take window-function results as arguments at the same query level. The fix is straightforward (a CTE that first marks the latest row per (account, month) using a window function, then aggregates against that flagged set), but the materialized view is not on the Phase 1 hot path. It's a Phase 8 (reports) artifact.

## Decision

- Phase 1 ships the schema, indexes, view, and trigger from the architecture doc, **excluding** the broken `monthly_account_balances` materialized view.
- The view is reintroduced in Phase 8 with a corrected definition (CTE-based) and accompanying refresh strategy (likely incremental refresh via the SimpleFIN sync hook).
- This decision is recorded so the deferral is intentional and revisitable.

## Consequences

**Positive**
- Phase 1 ships clean SQL that runs as written.
- The materialized-view design is revisited at the time it actually gets exercised (Phase 8 reports), giving us a chance to look at refresh cadence, concurrency, and incremental-refresh trade-offs in context.

**Negative**
- Until Phase 8, net-worth-history reports must aggregate live. For the initial single-user dataset (~42k transactions), live aggregation in Postgres is fast enough that this is not a real cost.

## Alternatives considered

- **Fix the SQL inline now and ship the materialized view in Phase 1.** Not wrong, but the view exists to serve a feature (net-worth charts) that doesn't exist yet. Pre-building it is a hack-shaped commitment to a refresh strategy we haven't designed. Rejected per the no-hacks charter.
- **Keep the broken SQL in the doc as a known TODO.** Misleading; the doc is the source of truth and shouldn't ship broken examples. Rejected. The doc has been updated to point at this ADR.
