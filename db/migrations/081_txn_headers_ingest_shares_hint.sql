-- =============================================================================
-- 081 — txn_headers ingest shares hint + provisional flag (ADR-0031 Phase 3d / Idea 2)
-- =============================================================================
--
-- Adds two columns to txn_headers so the SimpleFIN orchestrator can
-- persist the holdings-delta classifier's provisional share count
-- alongside the action + security hints already added by migration 076.
--
-- Why "provisional" is a separate boolean rather than overloading
-- a sentinel value:
--   * Future providers (OFX, CSV) may carry an authoritative share
--     count on the wire — they'd set the hint without the
--     provisional flag.
--   * The SPA editor reads the flag independently to decide whether
--     to pre-fill confidently or display a "(guess)" badge.
--   * Audit trail: a row that was attributed via snapshot delta vs
--     pulled directly off the wire is a meaningful distinction.
--
-- Snapshot-delta attribution algorithm (provider-side, opaque to
-- the DB): for each transaction whose ticker resolved cleanly to a
-- single-position delta between prior and current SimpleFIN
-- holdings[] snapshots, shares := |current_shares - prior_shares|
-- and action defaults to 'buy' (positive delta) or 'sell' (negative
-- delta) when the description classifier didn't already set one.
-- Tickers with multiple transactions in the same sync are
-- ambiguous and abstain (shares stays NULL).
--
-- Also updates resolved_transactions in the same script so the SPA
-- can render the new hints without a follow-up view migration.
-- security_invoker = true re-asserted per the 057 / 077 / 079
-- pattern.
-- =============================================================================

ALTER TABLE txn_headers
    ADD COLUMN ingest_shares_hint NUMERIC(28, 8) NULL,
    ADD COLUMN ingest_shares_provisional BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE txn_headers
    ADD CONSTRAINT ck_txn_headers_ingest_shares_hint_positive
    CHECK (ingest_shares_hint IS NULL OR ingest_shares_hint > 0);

ALTER TABLE txn_headers
    ADD CONSTRAINT ck_txn_headers_ingest_shares_provisional_requires_hint
    CHECK (NOT ingest_shares_provisional OR ingest_shares_hint IS NOT NULL);

COMMENT ON COLUMN txn_headers.ingest_shares_hint IS 'ADR-0031 Phase 3d (Idea 2): provider-classifier output, magnitude of the per-symbol position delta between prior + current SimpleFIN holdings[] snapshots. NULL when the provider could not attribute a clean single-transaction delta. Always positive — sign lives on ingest_action_hint (buy vs sell).';

COMMENT ON COLUMN txn_headers.ingest_shares_provisional IS 'ADR-0031 Phase 3d (Idea 2): true when ingest_shares_hint was inferred from the snapshot delta rather than provider-supplied. The SPA editor reads this to display a "(guess)" badge so the user can override. Future wire-level shares providers (OFX/CSV) would set the hint without the flag.';

CREATE OR REPLACE VIEW resolved_transactions AS
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
    h.provider_raw_payload,
    -- ADR-0031 Phase 3d (Idea 2): holdings-delta-inferred shares.
    -- NULL on rows that weren't snapshot-attributed (manual, non-
    -- investment, ambiguous multi-txn-per-ticker syncs).
    h.ingest_shares_hint,
    h.ingest_shares_provisional
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
