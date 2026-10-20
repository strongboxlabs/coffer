-- Verify uq_recurring_external_id is PER-LEDGER (migration 162), not global.
-- The rule: a Moneydance reminder external_id must be unique WITHIN a ledger
-- (idempotent re-import of that ledger's feed), but the SAME external_id must be
-- allowed in a DIFFERENT ledger — otherwise importing the same MD export into a
-- second ledger collides (the bug this migration fixes). NULL external_ids
-- (manual reminders) are exempt via the partial index.
--
-- Run with:
--   psql -U coffer -d coffer -v ON_ERROR_STOP=1 \
--        -f db/test/verify_recurring_external_id_per_ledger.sql
-- All assertions use plpgsql DO blocks; any failure aborts the script.
-- The ROLLBACK at the end discards the fixture.

BEGIN;

INSERT INTO ledgers (id, name) VALUES
    ('dddddddd-0000-0000-0000-dddddddddddd', 'Recurring EID Test Ledger A'),
    ('dddddddd-1111-1111-1111-dddddddddddd', 'Recurring EID Test Ledger B');

-- Baseline: one reminder with external_id 'md-reminder-1' in ledger A.
INSERT INTO recurring_transactions (ledger_id, external_id, start_date)
VALUES ('dddddddd-0000-0000-0000-dddddddddddd', 'md-reminder-1', DATE '2026-01-01');

-- Test 1 (allow): the SAME external_id in ledger B — the second-ledger import
-- case. Must succeed now that uniqueness is per-ledger.
INSERT INTO recurring_transactions (ledger_id, external_id, start_date)
VALUES ('dddddddd-1111-1111-1111-dddddddddddd', 'md-reminder-1', DATE '2026-01-01');

-- Test 2 (reject): the same external_id AGAIN in ledger A must still violate the
-- index — within-ledger idempotency is preserved.
DO $$
BEGIN
    BEGIN
        INSERT INTO recurring_transactions (ledger_id, external_id, start_date)
        VALUES ('dddddddd-0000-0000-0000-dddddddddddd', 'md-reminder-1', DATE '2026-02-01');
        RAISE EXCEPTION
            'FAIL: uq_recurring_external_id allowed a duplicate external_id within one ledger';
    EXCEPTION
        WHEN unique_violation THEN
            RAISE NOTICE 'OK: duplicate external_id within a ledger was rejected';
    END;
END $$;

-- Test 3 (allow): multiple NULL external_ids in one ledger — the partial index
-- exempts them (manually-created reminders have no source-system id).
INSERT INTO recurring_transactions (ledger_id, external_id, start_date) VALUES
    ('dddddddd-0000-0000-0000-dddddddddddd', NULL, DATE '2026-03-01'),
    ('dddddddd-0000-0000-0000-dddddddddddd', NULL, DATE '2026-03-02');

ROLLBACK;
