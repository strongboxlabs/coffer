-- =============================================================================
-- 218 — one fire, one occurrence, enforced by the database.
-- =============================================================================
--
-- WHY: `RemindersRepository.FireAsync` decides "has this (series, date) already been
-- committed?" with a SELECT, and inserts if it finds nothing. At READ COMMITTED two
-- callers both read nothing and both insert, and there is no constraint behind them —
-- the only index on those columns is mig 124's NON-unique (recurring_transaction_id,
-- ledger_id). Today the only caller is a person clicking Fire, so the window is small
-- and nobody has hit it. Reminder auto-post makes the second caller a timer, and the
-- thing being duplicated is a financial transaction.
--
-- Worse than a race in the abstract: FireAsync's idempotency SELECT and its skip check
-- both run BEFORE it opens its transaction, so they are not even repeatable-read
-- against the insert that follows them.
--
-- TWO MECHANISMS, BECAUSE THERE ARE TWO RACES.
--
--   1. FIRE vs FIRE — same table. A partial UNIQUE index settles it: the loser gets
--      23505 and the repository turns that into the same answer the sequential path
--      returns, so a race is idempotent rather than a 500.
--
--   2. FIRE vs SKIP — DIFFERENT TABLES. Fire reads recurring_occurrence_exceptions then
--      inserts into txn_headers; Skip reads txn_headers then inserts into
--      recurring_occurrence_exceptions. No constraint can span the pair, so an index
--      cannot help: interleaved, both checks pass and the slot ends up BOTH fired and
--      skipped — the app posts money the user just told it not to. That needs a lock
--      the two paths share, which is what reminder_occurrence_lock is for.
--
-- WHY AN ADVISORY LOCK AND NOT `SELECT ... FOR UPDATE` on the series row. Locking the
-- series serialises every occurrence of it against every other, including a catch-up
-- run posting a backlog — the case where throughput actually matters. The advisory key
-- is the (series, occurrence) SLOT, so two different dates in one series still proceed
-- in parallel while the one contended slot serialises. It is also transaction-scoped
-- (pg_advisory_xact_lock, not the session variant), so it cannot leak out of a
-- connection returned to the pool holding a lock forever — the failure mode that makes
-- session-level advisory locks a hazard in a pooled application.
--
-- IT IS A FUNCTION BECAUSE src/Api MAY NOT WRITE RAW SQL. Mapped through
-- HasDbFunction like every other Postgres primitive here.
--
-- =============================================================================

BEGIN;

-- -----------------------------------------------------------------------------
-- Fail loudly, naming the offender, rather than letting CREATE UNIQUE INDEX emit
-- its own message.
-- -----------------------------------------------------------------------------
-- An operator who hits this needs to know WHICH series and WHICH date, and that the
-- fix is to delete the duplicate transaction rather than to skip the migration.
-- Postgres's own "could not create unique index ... key is duplicated" names the index
-- and the key values as an opaque tuple, on an upgrade that has already half-applied.
--
-- This cannot fire on an install that has never raced, which is expected to be all of
-- them; it exists because "expected to be" is not "verified", and a silent CASCADE or a
-- DELETE picking an arbitrary winner would be resolving a money question by coin flip.
DO $$
DECLARE
    offender RECORD;
BEGIN
    SELECT recurring_transaction_id, occurrence_date, COUNT(*) AS n
      INTO offender
      FROM txn_headers
     WHERE recurring_transaction_id IS NOT NULL
       AND occurrence_date IS NOT NULL
       AND NOT is_recurring_template
     GROUP BY recurring_transaction_id, occurrence_date
    HAVING COUNT(*) > 1
     LIMIT 1;

    IF FOUND THEN
        RAISE EXCEPTION
            'Reminder series % already has % committed transactions for occurrence %. '
            'Migration 218 makes that pair unique. Delete the duplicate transaction(s) '
            'for this occurrence, keeping one, then re-run the upgrade.',
            offender.recurring_transaction_id, offender.n, offender.occurrence_date;
    END IF;
END $$;

-- -----------------------------------------------------------------------------
-- Fire vs fire.
-- -----------------------------------------------------------------------------
-- PARTIAL on three predicates, each load-bearing:
--   * recurring_transaction_id IS NOT NULL — ordinary transactions are unconstrained.
--   * occurrence_date IS NOT NULL — a header stamped with a series but no slot is not
--     an occurrence of anything.
--   * NOT is_recurring_template — belt and braces. A template is linked the OTHER
--     way, by recurring_transactions.template_header_id, and its own
--     recurring_transaction_id is NULL, so the IS NOT NULL predicate above already
--     excludes it today. Verified, not assumed: a test asserts the template's stamp is
--     null, because this predicate was written believing the opposite. It stays so that
--     stamping templates later — a reasonable change, it would make the link
--     symmetric — cannot silently make every series unfireable at its first occurrence.
CREATE UNIQUE INDEX uq_txn_headers_recurring_occurrence
    ON txn_headers (recurring_transaction_id, occurrence_date)
    WHERE recurring_transaction_id IS NOT NULL
      AND occurrence_date IS NOT NULL
      AND NOT is_recurring_template;

COMMENT ON INDEX uq_txn_headers_recurring_occurrence IS
    'One committed transaction per (reminder series, occurrence date). The constraint '
    'behind RemindersRepository.FireAsync''s idempotency, which was a bare SELECT-then-'
    'INSERT at READ COMMITTED. The template header is excluded: its own '
    'recurring_transaction_id is null (the link is recurring_transactions.template_header_id), '
    'and the NOT is_recurring_template predicate keeps that true if templates are ever stamped. '
    'Mig 124''s (recurring_transaction_id, ledger_id) index is a different leading pair '
    'and stays.';

-- -----------------------------------------------------------------------------
-- Fire vs skip.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION reminder_occurrence_lock(
    p_recurring_transaction_id UUID,
    p_occurrence_date          DATE
) RETURNS TABLE (locked BOOLEAN)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    -- hashtextextended over the pair, not the series alone: the unit of exclusion is
    -- the SLOT. Two different dates in one series do not contend, so a catch-up run
    -- posting a backlog is not serialised against itself.
    --
    -- A hash collision costs correctness NOTHING here — two unrelated slots would take
    -- turns instead of running together. That is the right trade for a lock: the
    -- failure mode of a collision is a little contention, never a missed exclusion.
    PERFORM pg_advisory_xact_lock(
        hashtextextended(
            p_recurring_transaction_id::text || ':' || p_occurrence_date::text, 0));

    -- A row, because EF binds set-returning functions. Callers ignore the value; the
    -- lock is the effect, and it is held until the caller's transaction ends.
    RETURN QUERY SELECT TRUE;
END $$;

COMMENT ON FUNCTION reminder_occurrence_lock(UUID, DATE) IS
    'Transaction-scoped advisory lock on one (reminder series, occurrence date) slot. '
    'Taken by both the fire and the skip paths so the two cannot interleave: they touch '
    'different tables, so no constraint can exclude them and only a shared lock can. '
    'Transaction-scoped so a pooled connection cannot be returned still holding it.';

GRANT EXECUTE ON FUNCTION reminder_occurrence_lock(UUID, DATE) TO coffer_app, coffer_service;

COMMIT;
