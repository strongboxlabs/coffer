-- 030_normalize_recon_status_and_add_cleared_audit.sql
-- =================================================================
--
-- Normalize `txn_headers.status` into a 3-state reconciliation
-- vocabulary + add audit columns for the cleared transition.
--
-- BEFORE
-- ------
-- `txn_headers.status` was a raw TEXT passthrough of Moneydance's
-- per-event `txn.stat` letter codes — `'c'` / `'C'` / `'x'` / `'X'`
-- for cleared (paper-check and online-banking eras of MD), `'R'`
-- for the rare legacy "reconciled" code, NULL for everything else.
-- The web client (`RegisterPage.tsx`) built a `CLEARED_STATUS_CODES`
-- set to interpret those letters as "cleared." Mutation went
-- through the override layer (`txn_header_overrides.status`).
--
-- Two unused 2-state columns lived alongside this:
-- `reconciled_at` and `reconciled_by_user_id` — added in migration
-- 022 anticipating a permanent "reconciled" state that, per the
-- user's MD domain definition, doesn't actually exist (MD's
-- "reconciling" is a workflow status, not a permanent terminal
-- state).
--
-- AFTER
-- -----
-- `status` is the canonical reconciliation column with a CHECK
-- constraint on three values:
--   * `uncleared`   — default, not yet matched against a statement
--   * `reconciling` — user has tentatively marked it during a
--                     reconciliation session (persists across
--                     sessions; functionally still uncleared for
--                     reporting, just a workflow / visual aid)
--   * `cleared`     — matched against a statement
--
-- Two new columns on `txn_headers` carry the cleared-transition
-- audit trail:
--   * `cleared_at`         — TIMESTAMPTZ, NULL until cleared
--   * `cleared_by_user_id` — UUID FK → users(id) ON DELETE SET NULL
--
-- A consistency CHECK ties them together: `(status = 'cleared')
-- ⇔ (cleared_at IS NOT NULL)`, so the row can't claim to be cleared
-- without an audit timestamp and can't carry a stale timestamp
-- after being unmarked.
--
-- The 2-state legacy columns (`reconciled_at`, `reconciled_by_user_id`)
-- are dropped — they had no callers and the new pair supersedes them.
--
-- Override-layer column dropped too
-- ---------------------------------
-- `txn_header_overrides.status` is dropped. Under the new model,
-- `status` is user-action data (a user clicks the badge to cycle
-- it) and lives directly on `txn_headers` — parallel to the
-- existing pattern for `reconciled_at` before this migration, where
-- a user-action timestamp was mutated on the header directly rather
-- than through the override row. The override layer is reserved for
-- fields where the user is overriding an imported value (payee,
-- memo, posted_at — fields with an "import said X, user wants Y"
-- distinction). Reconciliation status has no meaningful imported
-- value; the importer normalizes MD letter-codes on insert and
-- everything after that is user action.
--
-- Any rows that had an override-layer status are collapsed onto
-- the header before the column is dropped (step 0 below).
-- =================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- 0. Collapse override-layer status into the header.
--    Any user-set override wins over the raw imported value, matching
--    the COALESCE semantics the old view used.
-- ---------------------------------------------------------------------
UPDATE txn_headers h
SET status = o.status
FROM txn_header_overrides o
WHERE o.header_id = h.id
  AND o.status IS NOT NULL;

-- ---------------------------------------------------------------------
-- 1. Backfill existing values to the normalized vocabulary.
--    MD letter codes:
--      'c' / 'C' / 'x' / 'X' → 'cleared' (paper + online)
--      'R'                   → 'cleared' (rare legacy "reconciled";
--                                          collapses to cleared since
--                                          there's no permanent
--                                          reconciled state in our model)
--      NULL / '' / other     → 'uncleared'
--    The `else 'uncleared'` arm is deliberately broad — any
--    unexpected legacy value collapses safely; the user can re-mark
--    if a row drifts.
-- ---------------------------------------------------------------------
UPDATE txn_headers SET status = CASE
    WHEN status IN ('c', 'C', 'x', 'X') THEN 'cleared'
    WHEN status = 'R'                   THEN 'cleared'
    ELSE                                     'uncleared'
END;

-- ---------------------------------------------------------------------
-- 2. Tighten the column: DEFAULT + NOT NULL + CHECK.
-- ---------------------------------------------------------------------
ALTER TABLE txn_headers
    ALTER COLUMN status SET DEFAULT 'uncleared';

ALTER TABLE txn_headers
    ALTER COLUMN status SET NOT NULL;

ALTER TABLE txn_headers
    ADD CONSTRAINT txn_headers_status_valid
        CHECK (status IN ('uncleared', 'reconciling', 'cleared'));

-- ---------------------------------------------------------------------
-- 3. Drop the unused 2-state legacy columns.
--    These were added in migration 022 anticipating a permanent
--    "reconciled" state that doesn't exist in our model. No callers.
-- ---------------------------------------------------------------------
ALTER TABLE txn_headers DROP COLUMN reconciled_at;
ALTER TABLE txn_headers DROP COLUMN reconciled_by_user_id;

-- ---------------------------------------------------------------------
-- 4. Add the cleared-transition audit columns, backfill, then add the
--    consistency CHECK.
--
-- Backfill: every row that we normalized to status='cleared' in
-- step 1 needs a non-null cleared_at to satisfy the CHECK below.
-- The honest proxy is the row's posted_at — for an imported-already-
-- cleared row we have no original "when marked cleared" timestamp,
-- and posted_at means "cleared no later than this point in calendar
-- time." cleared_by_user_id stays NULL (the importer doesn't act as
-- a real user).
--
-- The CHECK gets added AFTER the backfill because Postgres validates
-- the constraint against every existing row at ADD CONSTRAINT time;
-- adding the CHECK before the backfill would fail on the populated
-- table where step 1 just produced status='cleared' rows.
-- ---------------------------------------------------------------------
ALTER TABLE txn_headers
    ADD COLUMN cleared_at         TIMESTAMPTZ,
    ADD COLUMN cleared_by_user_id UUID REFERENCES users(id) ON DELETE SET NULL;

UPDATE txn_headers
SET cleared_at = posted_at
WHERE status = 'cleared'
  AND cleared_at IS NULL;

ALTER TABLE txn_headers
    ADD CONSTRAINT txn_headers_status_cleared_consistency
        CHECK ((status = 'cleared') = (cleared_at IS NOT NULL));

-- ---------------------------------------------------------------------
-- 5. Refresh resolved_transactions view:
--      * `status` sourced directly from `h.status` (override layer no
--        longer involved)
--      * append `cleared_at` and `cleared_by_user_id` for the SPA's
--        badge rendering
--
--    CREATE OR REPLACE works because no existing column name / type /
--    position changes; the override-layer column drop happens AFTER
--    the view is rebuilt.
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW resolved_transactions AS
SELECT
    l.id,
    l.account_id,
    COALESCE(o.payee,            h.payee)                              AS payee,
    COALESCE(lo.leg_memo, l.leg_memo, o.memo, h.memo)                  AS memo,
    COALESCE(lo.amount,          l.amount)                             AS amount,
    COALESCE(o.posted_at,        h.posted_at)                          AS posted_at,
    COALESCE(o.transacted_at,    h.transacted_at)                      AS transacted_at,
    h.status                                                           AS status,
    COALESCE(o.is_hidden,        h.is_hidden, FALSE)                   AS is_hidden,
    (o.header_id IS NOT NULL OR lo.leg_id IS NOT NULL)                 AS has_overrides,
    l.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    l.investment_action,
    h.external_id,
    l.created_at,
    COALESCE(o.check_number,     h.check_number)                       AS check_number,
    other.id                                                           AS counterparty_id,
    CASE WHEN EXISTS (
        SELECT 1 FROM txn_legs g
        WHERE g.header_id = h.id AND g.posting_index > 0
    ) THEN h.id ELSE NULL END                                          AS txn_group_id,
    l.posting_index                                                    AS leg_index,
    other.account_id                                                   AS counterparty_account_id,
    account_path(other.account_id)                                     AS counterparty_account_name,
    ca.account_type                                                    AS counterparty_account_type,
    COALESCE(
        ARRAY(SELECT tg.name
              FROM txn_header_tags tt
              JOIN tags tg ON tg.id = tt.tag_id
              WHERE tt.header_id = h.id
              ORDER BY tg.name),
        ARRAY[]::TEXT[]
    )                                                                  AS tags,
    h.id                                                               AS header_id,
    h.cleared_at                                                       AS cleared_at,
    h.cleared_by_user_id                                               AS cleared_by_user_id
FROM txn_legs l
JOIN txn_headers h ON h.id = l.header_id
LEFT JOIN txn_header_overrides o ON o.header_id = h.id
LEFT JOIN txn_leg_overrides    lo ON lo.leg_id  = l.id
LEFT JOIN txn_legs other
    ON other.header_id = l.header_id
    AND other.posting_index = l.posting_index
    AND other.id != l.id
LEFT JOIN accounts ca ON ca.id = other.account_id;

ALTER VIEW resolved_transactions SET (security_invoker = true);

-- ---------------------------------------------------------------------
-- 6. Drop the override-layer status column. Safe to do now that the
--    view doesn't reference it and any user-set values were collapsed
--    onto the header in step 0.
-- ---------------------------------------------------------------------
ALTER TABLE txn_header_overrides DROP COLUMN status;

COMMIT;
