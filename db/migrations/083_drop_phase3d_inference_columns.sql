-- =============================================================================
-- 083 — drop Phase 3d holdings-based inference columns (ADR-0031)
-- =============================================================================
--
-- Reverts the columns added by migrations 081 + 082. The holdings
-- description matcher (SimpleFinHoldingsMatcher / Idea 1) and the
-- snapshot-delta classifier (SimpleFinHoldingsDelta / Idea 2)
-- proved unreliable in practice:
--
--   * Idea 1 recovered tickers from holdings[] descriptions but
--     left action null on the very institutions that motivated it
--     (a brokerage money-market fund / MD 529 don't carry an action
--     keyword in the transaction description).
--   * Idea 2's shares-from-delta inference required two snapshots
--     separated by an actual position change, never fired
--     reliably on real data, and "(guess)" provisional values
--     added more cognitive load than they removed.
--
-- The reality is payee + memo (post-migration heuristic that
-- splits SimpleFIN's payee from description) give the user enough
-- information to pick the right security from current holdings in
-- the editor. Manual security pick is the right contract; we
-- don't bake speculation into the data model.
--
-- Phase 3c (regex-based description classifier for brokerage-style
-- "BOUGHT (AAPL)" descriptions and the ingest_action_hint +
-- ingest_security_id columns from migration 076) is preserved —
-- that classifier works deterministically off in-description
-- tokens, not holdings inference.
--
-- The view must be redefined BEFORE dropping the columns
-- (Postgres won't drop a column a view references). DROP +
-- CREATE because CREATE OR REPLACE VIEW can only ADD columns
-- (Postgres-specific constraint).
-- =============================================================================

DROP VIEW IF EXISTS resolved_transactions;

CREATE VIEW resolved_transactions AS
SELECT l.id,
    l.account_id,
    COALESCE(o.payee, h.payee) AS payee,
    COALESCE(lo.leg_memo, l.leg_memo, o.memo, h.memo) AS memo,
    COALESCE(lo.amount, l.amount) AS amount,
    COALESCE(o.posted_at, h.posted_at) AS posted_at,
    COALESCE(o.transacted_at, h.transacted_at) AS transacted_at,
    h.status,
    COALESCE(o.is_hidden, h.is_hidden, false) AS is_hidden,
    o.header_id IS NOT NULL OR lo.leg_id IS NOT NULL AS has_overrides,
    l.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    h.action AS investment_action,
    h.external_id,
    l.created_at,
    COALESCE(o.check_number, h.check_number) AS check_number,
    other.id AS counterparty_id,
        CASE
            WHEN (EXISTS ( SELECT 1
               FROM txn_legs g
              WHERE g.header_id = h.id AND g.posting_index > 0)) THEN h.id
            ELSE NULL::uuid
        END AS txn_group_id,
    l.posting_index AS leg_index,
    other.account_id AS counterparty_account_id,
    account_path(other.account_id) AS counterparty_account_name,
    ca.account_type AS counterparty_account_type,
    COALESCE(ARRAY( SELECT tg.name
           FROM txn_header_tags tt
             JOIN tags tg ON tg.id = tt.tag_id
          WHERE tt.header_id = h.id
          ORDER BY tg.name), ARRAY[]::text[]) AS tags,
    h.id AS header_id,
    h.cleared_at,
    h.cleared_by_user_id,
    COALESCE(lo.leg_memo, l.leg_memo) AS leg_memo,
    COALESCE(o.memo, h.memo) AS header_memo,
    h.online_match_fitid,
    h.online_match_fi_id,
    h.online_match_status,
    h.online_match_type,
    h.online_match_orig_id,
    h.needs_review,
    COALESCE(l.security_id, other.security_id) AS security_id,
    s.ticker AS security_ticker,
    s.name AS security_name,
    COALESCE(l.quantity, other.quantity) AS quantity,
    COALESCE(l.unit_price, other.unit_price) AS unit_price,
    l.posting_role,
    h.ingest_action_hint,
    h.ingest_security_id,
    -- Migrations 078/079 (kept): per-transaction raw provider JSON
    -- for the "Show raw data" diagnostic modal.
    h.provider_raw_payload
   FROM txn_legs l
     JOIN txn_headers h ON h.id = l.header_id
     LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
     LEFT JOIN txn_legs other ON other.header_id = l.header_id AND other.posting_index = l.posting_index AND other.id <> l.id
     LEFT JOIN accounts ca ON ca.id = other.account_id
     LEFT JOIN securities s ON s.id = COALESCE(l.security_id, other.security_id);

ALTER VIEW resolved_transactions SET (security_invoker = true);

GRANT SELECT ON resolved_transactions TO coffer_app;
GRANT ALL    ON resolved_transactions TO coffer_service;

ALTER TABLE txn_headers
    DROP CONSTRAINT IF EXISTS ck_txn_headers_ingest_shares_hint_positive,
    DROP CONSTRAINT IF EXISTS ck_txn_headers_ingest_shares_provisional_requires_hint;

ALTER TABLE txn_headers
    DROP COLUMN IF EXISTS ingest_shares_hint,
    DROP COLUMN IF EXISTS ingest_shares_provisional,
    DROP COLUMN IF EXISTS ingest_security_ticker_hint;
