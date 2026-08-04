# 0004 — `balance_after` maintained by Postgres trigger

* Status: **Superseded by [ADR-0034](0034-header-walk-running-balance.md)** (2026-05-29)
* Date: 2026-05-08

> The trigger described here walked individual transaction rows
> ordered by `(posted_at, id)`. ADR-0022 normalised transactions into
> headers + legs, and the trigger was mechanically rewritten to walk
> legs — but a header with multiple cash legs on the same account
> (Slice A4 BuyXfr fan-out) produces intermediate balance values that
> aren't real cash states. ADR-0034 swaps the trigger family to walk
> headers, stores per-(header, account) balance in a dedicated table,
> and adopts `(posted_at, created_at, id)` as the canonical ordering
> for every transaction-time running-window computation.

## Context

The transaction register must show a running balance for each row. With years of history per account, it is unacceptable to recompute the balance on every read. The balance must be persisted on the row.

But persisting it correctly is harder than it looks:

- Inserts may arrive **out of date order** — the Moneydance import does not guarantee chronological order, and manual entries can be backdated.
- A transaction can become a duplicate (`is_merged_into` set) at any time, requiring all subsequent balances in that account to recompute.
- The same transaction can be soft-deleted (hidden), hard-deleted, or have its amount/date updated.
- Application code is not the only writer: imports, sync workers, manual SQL fixes, future tools — all must keep `balance_after` correct.

## Decision

`transactions.balance_after` is maintained by **PostgreSQL statement-level triggers** firing on `INSERT`, `UPDATE`, and `DELETE`. The trigger function:

1. Collects the affected rows from the transition tables (`new_rows` / `old_rows`).
2. On UPDATE, early-exits if no row's balance-relevant columns (`feed_amount`, `feed_posted_at`, `account_id`, `is_merged_into`) actually changed. (We can't push this filter into the trigger declaration because PostgreSQL forbids combining `AFTER UPDATE OF (columns)` with `REFERENCING ... TABLE` — "transition tables cannot be specified for triggers with column lists".)
3. For each affected `account_id`, finds `MIN(feed_posted_at)` across the dirty rows.
4. Calls `fn_recompute_balance_after(account_id, from_posted_at)`, which anchors at the latest active row strictly before that date (or the account's `opening_balance`) and recomputes every active row from there forward using a window-function `SUM`.
5. Skips re-firing on its own UPDATE of `balance_after` via a `pg_trigger_depth() > 1` short-circuit at the top of the function body.

`balance_after` is computed from `feed_amount` only. Override-amount values are ignored by the trigger.

## Consequences

**Positive**
- Correctness is enforced at the database, regardless of the writer.
- Out-of-order inserts work without special-casing in the importer.
- Soft-delete via `is_merged_into` participates correctly in balance recomputation.
- The trigger is independently testable. End-to-end test once lived in `db/test/verify_balance_trigger.sql`; retired in mig 102 when the trigger family was dropped — coverage moved to `BalanceConsistencyTests` and per-writer integration tests under `tests/Api.Tests/Integration/Transactions/`.

**Negative**
- A bulk insert of N rows for one account triggers a recompute over the affected window once. For the Phase 2 import (~42k rows across ~770 accounts) this is a single batch per account, acceptable. If we ever need to bulk-load millions of rows, we will disable the trigger, populate, then re-enable and run a one-shot recompute.
- `balance_after` does not reflect override-amount edits. Documented as a known limitation; override-amount is rare per the spec. If real use exposes this gap, the trigger can be extended to also fire on `transaction_overrides` changes.

## Alternatives considered

- **Compute on read via a window function.** Cheap for one account with light history; quadratic for big accounts and infeasible for cursor pagination. Rejected.
- **Maintain in application code.** Loses correctness when any non-app writer touches the table. Rejected.
- **Materialized view of running balances.** Adds a refresh dance and doesn't solve the cursor-pagination need (we want the balance pre-computed on the row, not in a separate structure). Rejected.
