-- Phase 2 PR 2.7: bring `csnap` price snapshots and `reminder` items into the
-- importer. Two schema changes; the rest of the work is mapper / pipeline code
-- that ships alongside this migration.
--
-- 1) Widen `security_prices` to OHLCV + volume.
--    The csnap importer translates each Moneydance `csnap` item into one
--    `security_prices` row. csnap carries `rate` (closing price), `hi`/`lo`
--    (intraday high/low), and `vol` (volume) — useful for charting and
--    gain/loss analysis once the UI lands. The original table only had
--    `price`; this widens it without breaking existing reads.
--
--    High/low are nullable because manually-entered MD prices typically only
--    carry the close. Volume uses BIGINT (liquid ETFs exceed 2^31 shares
--    traded). Currency-denominated values keep NUMERIC(19,4).
--
-- 2) Add `external_id` to `recurring_transactions` for idempotent re-runs.
--    Reminders carry an MD UUID; without storing it, the importer has no
--    way to match an incoming reminder to the row it produced last run.
--    Mirrors the `external_id` pattern already on `accounts` (mig 009)
--    and `securities` (mig 008).

-- ---------------------------------------------------------------------------
-- 1) security_prices OHLCV
-- ---------------------------------------------------------------------------
ALTER TABLE security_prices
    ADD COLUMN high   NUMERIC(19, 4),
    ADD COLUMN low    NUMERIC(19, 4),
    ADD COLUMN volume BIGINT,
    ADD CONSTRAINT security_prices_high_low_consistent
        CHECK (high IS NULL OR low IS NULL OR high >= low),
    ADD CONSTRAINT security_prices_volume_nonneg
        CHECK (volume IS NULL OR volume >= 0);

COMMENT ON COLUMN security_prices.price IS
    'Closing price (Moneydance''s `rate`). Required.';
COMMENT ON COLUMN security_prices.high IS
    'Intraday high (Moneydance''s `hi`). NULL if the snapshot didn''t carry one.';
COMMENT ON COLUMN security_prices.low IS
    'Intraday low (Moneydance''s `lo`). NULL if the snapshot didn''t carry one.';
COMMENT ON COLUMN security_prices.volume IS
    'Share volume traded that day (Moneydance''s `vol`). NULL if absent.';

-- ---------------------------------------------------------------------------
-- 2) recurring_transactions.external_id
-- ---------------------------------------------------------------------------
ALTER TABLE recurring_transactions
    ADD COLUMN external_id TEXT;

CREATE UNIQUE INDEX uq_recurring_external_id
    ON recurring_transactions(external_id)
    WHERE external_id IS NOT NULL;

COMMENT ON COLUMN recurring_transactions.external_id IS
    'Source-system identifier. For Moneydance imports this is the raw MD '
    'reminder UUID; lets re-runs upsert the same template idempotently. '
    'NULL for reminders created manually through the API.';
