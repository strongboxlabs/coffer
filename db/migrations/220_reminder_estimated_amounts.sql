-- =============================================================================
-- 220 — a reminder can estimate its amount from its own recent history.
-- =============================================================================
--
-- WHY: a reminder carries ONE fixed amount on its template. Real recurring bills do not.
-- A utility bill, a variable-rate payment, a usage-based subscription is known in SHAPE
-- and unknown in VALUE until it arrives, so today the user either types a number that
-- will be wrong or leaves it at zero and loses the agenda's forecast entirely.
--
-- The estimate is the average of the last N committed occurrences of that same series,
-- N chosen by the user, default 3. Fewer than N are used when fewer exist; ZERO
-- committed occurrences means no estimate at all rather than a guess from nothing.
--
-- ONE NULLABLE COLUMN, not a bool plus an int. NULL = off, N = on with sample size N,
-- so there is no representable state "estimating from an unset N" — the same convention
-- auto_commit_days_before already uses on this table.
--
-- NULLABLE ALSO BECAUSE OF SNAPSHOT RESTORE. Restore inserts through
-- jsonb_populate_recordset over the whole row (mig 193), so a payload written before
-- this column existed materializes it as NULL and supplies NULL explicitly — a column
-- default would never be consulted. NOT NULL here would break restore of every existing
-- snapshot.
--
-- A CHECK, unlike auto_commit_days_before, which has none and is hand-validated in three
-- endpoint copies. This column is new, so the constraint costs nothing and closes that
-- gap on day one. 24 is two years of a monthly bill; past that, "an average of recent
-- occurrences" is not describing recent behaviour.
--
-- THE FUNCTION EXISTS BECAUSE src/Api MAY NOT WRITE RAW SQL and this query cannot be
-- LINQ. It needs the last N rows PER SERIES, and EF will not translate a Take whose
-- limit comes from an outer column. The alternative — load every committed occurrence
-- for the ledger and reduce in C# — is unbounded in history (a decade-old daily series
-- is thousands of rows) on a path the scheduler hits every tick.
--
-- IT RETURNS SUM AND COUNT, NEVER AN AVERAGE. The division is the one lossy step, so it
-- happens in C# beside the single rounding call, at the destination scale that
-- ck_txn_legs_amount_scale_2 (mig 159) pins to 2dp. Rounding in SQL and again on write
-- would be the double-rounding defect mig 209 was written to fix.
--
-- HIDDEN OCCURRENCES ARE EXCLUDED BEFORE THE WINDOW, NOT AFTER, and the order matters.
-- Deleting a reminder occurrence now SOFT-HIDES it (mig 218 made the (series, date)
-- stamp an idempotency key, so the row has to survive), which means a deleted occurrence
-- still carries its stamp. Ranking first and filtering after would let three deletions
-- silently starve an estimate that has plenty of real history behind them. Filtering
-- first means "fewer than N" only ever means the series is young.
-- =============================================================================

BEGIN;

ALTER TABLE recurring_transactions
    ADD COLUMN IF NOT EXISTS estimate_sample_count INTEGER;

COMMENT ON COLUMN recurring_transactions.estimate_sample_count IS
    'NULL = the template amount is used as-is. N = the amount shown and posted is the '
    'average of the last N committed, non-hidden occurrences of this series (fewer if '
    'fewer exist; none at all means no estimate). Only for a single-posting, non-loan, '
    'bank-shape series — a split has no single amount to estimate, and a loan payment is '
    'computed from its terms and current balance, which is real information an average '
    'would only degrade.';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conname = 'ck_recurring_transactions_estimate_sample_count'
    ) THEN
        ALTER TABLE recurring_transactions
            ADD CONSTRAINT ck_recurring_transactions_estimate_sample_count
                CHECK (estimate_sample_count IS NULL
                       OR estimate_sample_count BETWEEN 1 AND 24);
    END IF;
END $$;

COMMENT ON CONSTRAINT ck_recurring_transactions_estimate_sample_count
    ON recurring_transactions IS
    'A sample size of zero would mean "estimate from nothing"; the API rejects above 24 '
    'with the same bound rather than clamping, because this column ships with its cap '
    'and so has no legacy rows to keep editable.';

-- Cheap early-out: the resolver asks "does this ledger have any estimating series?" on
-- every agenda load and every scheduler tick, and the answer is usually no.
CREATE INDEX IF NOT EXISTS idx_recurring_transactions_estimated
    ON recurring_transactions (ledger_id)
    WHERE estimate_sample_count IS NOT NULL;

-- -----------------------------------------------------------------------------
-- The samples behind one estimate.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION reminder_estimate_samples(
    p_ledger_id                UUID,
    p_recurring_transaction_id UUID
) RETURNS TABLE (
    recurring_transaction_id UUID,
    sample_count             INT,
    sample_sum               NUMERIC
)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    WITH candidates AS (
        SELECT r.id                AS series_id,
               h.id                AS header_id,
               r.estimate_sample_count,
               -- The occurrence's own net on the series' SOURCE account: the figure the
               -- agenda already shows for a fired occurrence, so an estimate is an
               -- average of exactly what the user has been seeing.
               (SELECT COALESCE(SUM(l.amount), 0)
                  FROM txn_legs l
                 WHERE l.header_id = h.id
                   AND l.account_id = r.source_account_id) AS net,
               ROW_NUMBER() OVER (
                   PARTITION BY r.id
                   ORDER BY h.occurrence_date DESC, h.id DESC
               ) AS rn
          FROM recurring_transactions r
          JOIN txn_headers h
            ON h.recurring_transaction_id = r.id
           AND h.ledger_id = r.ledger_id
           AND h.occurrence_date IS NOT NULL
           AND NOT h.is_recurring_template
           -- Excluded BEFORE the window: see the header. A deleted occurrence is not
           -- evidence of what the bill costs, and must not consume a sample slot.
           AND NOT h.is_hidden
           AND h.is_merged_into IS NULL
         WHERE r.ledger_id = p_ledger_id
           AND r.estimate_sample_count IS NOT NULL
           AND r.source_account_id IS NOT NULL
           AND (p_recurring_transaction_id IS NULL
                OR r.id = p_recurring_transaction_id)
    )
    SELECT series_id, COUNT(*)::INT, SUM(net)
      FROM candidates
     WHERE rn <= estimate_sample_count
     GROUP BY series_id;
$$;

COMMENT ON FUNCTION reminder_estimate_samples(UUID, UUID) IS
    'The last N committed, non-hidden, non-merged occurrences of each estimating series '
    'on a ledger, as a COUNT and a SUM of their source-side nets — never an average. The '
    'division and its single rounding happen in C# at the 2dp destination scale, because '
    'rounding here and again on write is the double-rounding defect mig 209 fixed. '
    'p_recurring_transaction_id NULL scopes to the whole ledger, so the agenda and the '
    'fire path read ONE implementation (the balance_walk scoping convention, mig 211).';

GRANT EXECUTE ON FUNCTION reminder_estimate_samples(UUID, UUID)
    TO coffer_app, coffer_service;

COMMIT;
