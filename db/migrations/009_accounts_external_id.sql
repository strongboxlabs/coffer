-- Phase 2 PR 2.4: track the source-system identifier on accounts so the
-- Moneydance importer can be re-run idempotently. Mirrors what migration
-- 008 did for `securities`. The `external_id` is the raw MD UUID for
-- accounts originating from a Moneydance export and stays NULL otherwise.

ALTER TABLE accounts
    ADD COLUMN external_id TEXT;

CREATE UNIQUE INDEX uq_accounts_external_id
    ON accounts(external_id)
    WHERE external_id IS NOT NULL;
