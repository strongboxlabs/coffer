-- Promote the FITID lookup index to UNIQUE (slice 2c.2).
--
-- Migration 034 created `idx_txn_headers_online_match_lookup` as a
-- non-unique partial index over (ledger_id, online_match_fi_id,
-- online_match_fitid). That gave us a fast lookup but no
-- correctness guarantee — concurrent SimpleFIN sync calls against
-- the same connection could each see "no match" for the same FITID
-- and both INSERT, producing duplicate register rows.
--
-- This migration:
--   1. Deduplicates any existing duplicate-FITID rows by keeping
--      the oldest per (ledger_id, fi_id, fitid) group. Cascade
--      cleans up txn_legs / sync_run_promotions / overrides via
--      the FK ON DELETE rules from prior migrations.
--   2. Drops the non-unique index, replaces it with a UNIQUE
--      partial index over the same columns. From now on, two
--      concurrent INSERTs of the same FITID raise a unique-
--      violation; the SyncService catches that and counts the
--      losing INSERT as `alreadyKnown` (slice 2c.2 in
--      `SimpleFinSyncService.cs`).
--
-- The dedup step is a no-op against a clean DB and harmless to
-- re-apply against any DB; the UNIQUE index is the load-bearing
-- correctness change.

-- ---------------------------------------------------------------------------
-- 1) Dedup pre-step. ROW_NUMBER over the partition keeps the
--    OLDEST row (lowest created_at, then lowest id) — that's the
--    canonical "first inserted" row, the one we want to retain.
-- ---------------------------------------------------------------------------
WITH ranked AS (
    SELECT id, ROW_NUMBER() OVER (
        PARTITION BY ledger_id, online_match_fi_id, online_match_fitid
        ORDER BY created_at, id
    ) AS rn
    FROM txn_headers
    WHERE online_match_fitid IS NOT NULL
)
DELETE FROM txn_headers
 WHERE id IN (SELECT id FROM ranked WHERE rn > 1);

-- ---------------------------------------------------------------------------
-- 2) Replace the non-unique index with the UNIQUE one. The
--    `WHERE online_match_fitid IS NOT NULL` predicate excludes
--    manual / non-OFX rows from the constraint — only feed-sourced
--    rows must be unique on this triple.
-- ---------------------------------------------------------------------------
DROP INDEX idx_txn_headers_online_match_lookup;

CREATE UNIQUE INDEX uq_txn_headers_online_match
    ON txn_headers (ledger_id, online_match_fi_id, online_match_fitid)
    WHERE online_match_fitid IS NOT NULL;

COMMENT ON INDEX uq_txn_headers_online_match IS
    'Per-ledger uniqueness for OFX-style FITID pairs (slice 2c.2). '
    'Promotes the migration-034 lookup index to UNIQUE so concurrent '
    'sync calls cannot insert duplicate register rows for the same '
    'bank-side transaction. SyncService catches the resulting '
    'unique-violation as alreadyKnown.';
