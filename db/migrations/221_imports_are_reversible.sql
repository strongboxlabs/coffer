-- =============================================================================
-- 221 — a transaction records the import that created it, so an import can be undone.
-- =============================================================================
--
-- WHY: file imports have no dedup and, by decision, will not get one. CSV rows carry
-- no issuer-assigned id (unlike OFX's FITID), so any per-row identity has to be
-- derived from content — and content cannot distinguish two genuinely identical
-- transactions (same date, same amount, same merchant). A content hash collapses them
-- and silently eats a real row; a hash including the row's ordinal duplicates
-- everything the moment a download window shifts. Neither is worth having in a ledger
-- that gets reconciled against real balances.
--
-- So the answer to "I uploaded that twice" is not prevention-by-guessing, it is UNDO.
-- That requires knowing which rows a given import created, and nothing recorded it:
-- txn_headers carries `provider_key` (WHICH provider) but not WHICH RUN, so a second
-- upload produced rows indistinguishable from the first — same provider, same dates,
-- same amounts. Cleanup meant hunting duplicates by eye.
--
-- `ledger_operation_id` closes that. The orchestrator already opens a
-- `ledger_operations` row per import (family 'ingest') and already returns its id to
-- the caller; it simply never stamped it on the rows. With the stamp, the register can
-- filter to one import and the existing bulk-delete removes exactly that set.
--
-- ===== ON DELETE SET NULL, AND THIS IS NOT A STYLE CHOICE =====
--
-- CASCADE here would DELETE MONEY. `AuditRetentionService` prunes `ledger_operations`
-- older than Api:AuditRetentionDays (default 180) with `ExecuteDeleteAsync` — a
-- set-based DELETE straight to Postgres, no change tracking, no app-level hook that
-- could veto it. Under CASCADE, a log-retention job would take every transaction
-- imported more than 180 days ago with it, and the enforcement would be in the
-- database where no C# guard is even consulted. The sibling children of this table
-- (ledger_operation_errors, _promotions) DO cascade, correctly — they are logs. This
-- one is not.
--
-- Consequence, accepted: the stamp EXPIRES when its operation is pruned, so imports
-- older than the retention window can no longer be undone as a set. That is the right
-- trade — undo matters in the minutes after a mistaken upload, not in month seven —
-- and the alternative was a retention job that deletes ledgers.
--
-- NULLABLE, permanently. A hand-entered transaction was created by no import, and
-- every row that predates this migration has no import to name. Backfilling a
-- sentinel would invent provenance that does not exist; nothing here writes rows.
--
-- DDL ONLY. No data is read, written or moved.

BEGIN;

ALTER TABLE txn_headers
    ADD COLUMN ledger_operation_id UUID
        REFERENCES ledger_operations(id) ON DELETE SET NULL;

-- Partial: the column is NULL for every manual entry and every pre-221 row, which is
-- most of the table on an established ledger. The only queries that use it filter to
-- one import, so indexing the NULLs would be pure write cost.
CREATE INDEX idx_txn_headers_ledger_operation
    ON txn_headers (ledger_operation_id)
    WHERE ledger_operation_id IS NOT NULL;

COMMENT ON COLUMN txn_headers.ledger_operation_id IS
    'The ledger_operations row (family ''ingest'') whose run created this transaction, '
    'or NULL for a manual entry, a pre-221 row, or an import whose operation has since '
    'been pruned by audit retention. Exists so an import can be undone as a set, which '
    'is how file imports handle re-uploads instead of dedup. ON DELETE SET NULL is '
    'load-bearing: retention deletes ledger_operations, and CASCADE would delete the '
    'transactions with them (mig 221 header).';

COMMIT;
