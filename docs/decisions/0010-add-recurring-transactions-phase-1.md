# 0010 — Add `recurring_transactions` to Phase 1 schema

* Status: Accepted
* Date: 2026-05-08

## Context

The architecture doc as originally written did not include a `recurring_transactions` table. The Moneydance export contains 19 `reminder` rows representing recurring transaction templates (monthly payments, auto-pays, etc.). These are user-meaningful data, not internal Moneydance bookkeeping.

If we don't model them in Phase 1, the importer either drops them on the floor or stages them in a side channel awaiting a later schema. Both are hacks. Adding the table now is one extra `CREATE TABLE`.

## Decision

Add `recurring_transactions` to the Phase 1 schema, with columns sufficient to round-trip the Moneydance reminder data:

- Source/target accounts.
- Description, memo, amount.
- `frequency` (`daily`/`weekly`/`monthly`/`yearly`/`custom`) plus `monthly_day` / `weekly_dow` / `interval_units`.
- `start_date`, `end_date`, `next_due_date`, `last_acknowledged_date`.
- `is_loan_reminder`, `is_active`, `origin`.

Importer (Phase 2) populates this table from the 19 `reminder` rows. UI for managing recurring transactions is deferred — the schema is in place so importer doesn't lose data.

## Consequences

**Positive**
- The Phase 2 importer is a complete round-trip for the reminder data with no special-casing.
- A useful feature gets schema-ready ahead of UI work; when the UI lands it's a presentation-layer change.
- One ADR documents the deviation from the architecture doc.

**Negative**
- The table sits unused at the application layer until the recurring-transaction UI ships. The cost is one CREATE TABLE statement.

## Alternatives considered

- **Skip on import; add the table later.** Loses user data on the import; later we'd have no way to reconstruct it short of re-importing from JSON. Rejected.
- **Stash reminders in a generic "import_orphans" table.** Worse than skipping — pretends the data is preserved but hides it where no code path will ever look. Rejected.
- **Build the full recurring-transaction generator now (cron job that materializes upcoming instances).** Out of Phase 1 scope. Deferred.
