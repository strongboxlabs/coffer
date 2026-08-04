# 0003 — Immutable feed values + separate `transaction_overrides` layer

* Status: Accepted
* Date: 2026-05-08

## Context

A bank-feed-driven personal-finance app has to reconcile two truths:

1. **What the bank reported.** The raw `payee`, `memo`, `amount`, `posted_at`, `status` returned by the feed.
2. **What the user thinks of it.** Renamed payee ("Whole Foods" instead of "WHOLEFDS"), categorized split, hidden, edited memo, custom date.

Naive designs pick one. If you mutate the feed values when the user edits, you lose the ability to recover original data, and re-running rules over historical data is destructive. If you keep only the feed values, every UI surface and report has to apply user edits at read time.

## Decision

Two tables, joined at read time through a SQL view:

- `transactions` holds **only feed values**, prefixed `feed_*`. Never modified after insert by user actions.
- `transaction_overrides` holds **only user edits**. NULL columns mean "use the feed value".
- `resolved_transactions` is a `LEFT JOIN` view that `COALESCE`s overrides over feed values and exposes a `has_overrides` flag.

All application reads, reports, and UI go through `resolved_transactions`. The raw `transactions` table is touched only by the importer and the sync service.

## Consequences

**Positive**
- "Reset to original" is a `DELETE FROM transaction_overrides WHERE transaction_id = ?`. Trivial.
- Auto-categorization rules can be re-run idempotently; they only insert/update override rows.
- `has_overrides` makes it cheap to show a "modified" indicator in the register.
- Auditability: the original feed values are always present and untampered.
- Simplifies bank-feed reconciliation: when the same external_id is fetched again, we don't have to disambiguate "did this change because the bank changed it or because the user edited it?".

**Negative**
- A `LEFT JOIN` on every read. Acceptable; the join is on a primary key and the override table is sparse.
- One more table to maintain.
- A user-amount-override creates a discrepancy with `balance_after`, which is computed from `feed_amount`. We accept this — overrides on amount are documented as rare. See [0004-balance-after-trigger.md](0004-balance-after-trigger.md).

## Alternatives considered

- **Mutate `transactions` directly, store original in a sidecar `transaction_history`.** Loses the "always-recoverable original" property and requires every read to reconstruct history if the user wants to compare. Rejected.
- **Mutate `transactions`, drop original entirely.** The user's auto-categorization work is destructive of bank-reported data, and re-syncing the same window from the bank can't deduplicate against original feed values. Rejected.
- **Event-sourced `transaction_events` log.** Stronger model but enormous overkill for a single-user finance app. Rejected as YAGNI.
