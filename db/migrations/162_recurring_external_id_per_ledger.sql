-- 162 — recurring_transactions.external_id uniqueness is PER-LEDGER, not global.
--
-- uq_recurring_external_id (migration 013) was UNIQUE(external_id) WHERE
-- external_id IS NOT NULL — a GLOBAL constraint, despite the schema-doc claim
-- that it was "already per-ledger by transitive scoping". A UNIQUE index on
-- external_id alone is NOT scoped by the row's ledger: two ledgers seeded from
-- the same Moneydance export carry identical MD reminder external_ids and
-- collide on the SECOND ledger's import (surfaced by importing/reconciling the
-- same export into a fresh ledger — the reminder step failed with
-- 23505 uq_recurring_external_id).
--
-- external_id only needs to be unique WITHIN a ledger (idempotent re-import of
-- that ledger's own feed). Re-scope the uniqueness to (ledger_id, external_id).
-- The partial WHERE keeps NULL external_ids (manually-created reminders) exempt.

DROP INDEX IF EXISTS uq_recurring_external_id;

CREATE UNIQUE INDEX uq_recurring_external_id
    ON recurring_transactions (ledger_id, external_id)
    WHERE external_id IS NOT NULL;
