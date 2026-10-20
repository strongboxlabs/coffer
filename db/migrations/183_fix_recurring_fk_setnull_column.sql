-- =============================================================================
-- 183 — fix txn_headers -> recurring_transactions ON DELETE SET NULL (23502)
-- =============================================================================
--
-- txn_headers has a COMPOSITE fk (recurring_transaction_id, ledger_id) ->
-- recurring_transactions(id, ledger_id) declared ON DELETE SET NULL. With no
-- column list, Postgres nulls EVERY referencing column on parent delete --
-- including ledger_id, which is NOT NULL. So deleting a recurring_transactions
-- row that has generated txn_headers fails with:
--   23502: null value in column "ledger_id" of relation "txn_headers"
--
-- This fires on the snapshot restore (fn_ledger_snapshot_restore deletes
-- recurring_transactions while its generated headers still exist) AND on any
-- plain "delete a recurring transaction that has generated entries". It was
-- masked until the snapshot OOM (mig 179) was fixed and restore actually ran.
--
-- Fix (PG15+ column-specific SET NULL): null ONLY recurring_transaction_id, not
-- ledger_id. Deleting a recurring template detaches its generated entries
-- (recurring_transaction_id -> NULL) and leaves them in the ledger, which is the
-- intended behavior. The composite fk is MATCH SIMPLE, so a (NULL, ledger_id)
-- pair is not re-checked. Postgres server_version 16.x on all environments.
-- =============================================================================

ALTER TABLE txn_headers
    DROP CONSTRAINT txn_headers_recurring_transaction_fkey;

ALTER TABLE txn_headers
    ADD CONSTRAINT txn_headers_recurring_transaction_fkey
        FOREIGN KEY (recurring_transaction_id, ledger_id)
        REFERENCES recurring_transactions(id, ledger_id)
        ON DELETE SET NULL (recurring_transaction_id);
