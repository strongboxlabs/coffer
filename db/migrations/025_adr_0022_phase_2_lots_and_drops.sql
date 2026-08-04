-- 025_adr_0022_phase_2_lots_and_drops.sql
--
-- ADR-0022 Phase 2: retarget the surviving FK dependents of the
-- legacy `transactions` table, drop it and its sibling override + tag
-- tables, and drop the ADR-0019 symmetric-pair trigger.
--
-- Dependents of `transactions(id)` going into this migration:
--   * `lots.transaction_id`            -> retarget to `txn_legs.id`
--   * `merge_candidates.incoming_txn_id` -> retarget to `txn_headers.id`
--   * `merge_candidates.existing_txn_id` -> retarget to `txn_headers.id`
--   * `transaction_overrides.transaction_id` -> dropped with the table
--   * `transaction_tags.transaction_id`      -> dropped with the table
--   * `transactions.is_merged_into` (self)   -> dropped with the table
--
-- Existing rows in lots / merge_candidates reference legacy txn ids
-- that won't exist after the drop. We truncate both up-front; the
-- investment importer's re-import populates lots, and merge_candidates
-- is a short-lived "pending review" queue (no production data lives
-- there).
--
-- Old running-balance trigger functions (`fn_recompute_balance_after`,
-- `fn_trg_balance_after`) defined on the legacy `transactions` table
-- become orphaned when the triggers drop with the table; we DROP
-- FUNCTION them explicitly so they don't linger.

BEGIN;

-- ---------------------------------------------------------------------------
-- 1) Clear stale data on the surviving dependents.
-- ---------------------------------------------------------------------------
TRUNCATE lots;
TRUNCATE merge_candidates;

-- ---------------------------------------------------------------------------
-- 2) lots: transaction_id -> leg_id (txn_legs)
-- ---------------------------------------------------------------------------
ALTER TABLE lots DROP CONSTRAINT lots_transaction_id_fkey;
ALTER TABLE lots RENAME COLUMN transaction_id TO leg_id;
ALTER TABLE lots
    ADD CONSTRAINT lots_leg_id_fkey
    FOREIGN KEY (leg_id) REFERENCES txn_legs(id) ON DELETE RESTRICT;

-- Re-create the supporting index on the new column name. The old
-- name didn't have a dedicated index (only the FK constraint), but
-- the lots writer issues `DELETE FROM lots WHERE leg_id = ANY(...)`
-- on every re-import, so an index keyed on leg_id is worth the cost.
CREATE INDEX idx_lots_leg_id ON lots(leg_id);

-- ---------------------------------------------------------------------------
-- 3) merge_candidates: {incoming,existing}_txn_id -> *_header_id
-- ---------------------------------------------------------------------------
ALTER TABLE merge_candidates
    DROP CONSTRAINT merge_candidates_incoming_txn_id_fkey,
    DROP CONSTRAINT merge_candidates_existing_txn_id_fkey;

ALTER TABLE merge_candidates RENAME COLUMN incoming_txn_id TO incoming_header_id;
ALTER TABLE merge_candidates RENAME COLUMN existing_txn_id TO existing_header_id;

ALTER TABLE merge_candidates
    ADD CONSTRAINT merge_candidates_incoming_header_id_fkey
    FOREIGN KEY (incoming_header_id) REFERENCES txn_headers(id) ON DELETE CASCADE,
    ADD CONSTRAINT merge_candidates_existing_header_id_fkey
    FOREIGN KEY (existing_header_id) REFERENCES txn_headers(id) ON DELETE CASCADE;

-- ---------------------------------------------------------------------------
-- 4a) Re-create RLS policies that previously referenced `transactions`
--     so they compose through the new tables instead.
-- ---------------------------------------------------------------------------
-- lots: visibility derives from txn_legs (which itself composes
-- through txn_headers and user_ledger_grants per migration 022).
DROP POLICY IF EXISTS lots_per_user ON lots;
CREATE POLICY lots_per_user ON lots FOR ALL TO coffer_app
    USING      (leg_id IN (SELECT id FROM txn_legs))
    WITH CHECK (leg_id IN (SELECT id FROM txn_legs));

-- merge_candidates: visibility derives from txn_headers.
DROP POLICY IF EXISTS merge_candidates_per_user ON merge_candidates;
CREATE POLICY merge_candidates_per_user ON merge_candidates FOR ALL TO coffer_app
    USING      (incoming_header_id IN (SELECT id FROM txn_headers))
    WITH CHECK (incoming_header_id IN (SELECT id FROM txn_headers));

-- ---------------------------------------------------------------------------
-- 4b) Drop the legacy tables.
--    Order: transaction_tags + transaction_overrides first (FK at
--    transactions); then transactions itself. The drop of `transactions`
--    cascades to its triggers (trg_txn_balance_after_* and
--    trg_counterparty_symmetric), which is what makes step 5 safe — the
--    leftover trigger functions stop being referenced.
-- ---------------------------------------------------------------------------
DROP TABLE IF EXISTS transaction_tags;
DROP TABLE IF EXISTS transaction_overrides;
DROP TABLE IF EXISTS transactions;

-- ---------------------------------------------------------------------------
-- 5) Drop the orphaned trigger functions. With the table gone, no
--    triggers reference them, so the DROP succeeds.
-- ---------------------------------------------------------------------------
DROP FUNCTION IF EXISTS fn_validate_counterparty_symmetric();
DROP FUNCTION IF EXISTS fn_recompute_balance_after(UUID, TIMESTAMPTZ);
DROP FUNCTION IF EXISTS fn_trg_balance_after();

COMMIT;
