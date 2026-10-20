-- =============================================================================
-- 082 — txn_headers ingest security ticker hint (ADR-0031 Phase 3d follow-up)
-- =============================================================================
--
-- Persists the raw provider ticker symbol recovered by the
-- description classifier or holdings matcher alongside the
-- already-resolved ingest_security_id from migration 076.
--
-- WHY a separate column from ingest_security_id:
--   * provider_security_mappings (the ticker → security_id table)
--     is empty for first-time-seen tickers. ingest_security_id
--     stays NULL even when the matcher CONFIDENTLY recovered the
--     ticker text — the editor's typeahead has nothing to pre-fill.
--   * Persisting the ticker text gives the editor a fallback:
--     "we couldn't resolve MMFA to a security in your ledger, but
--     here's the ticker — pick a security and we'll record the
--     mapping." Same UX as the existing providerSecurityHint flow
--     except now the hint survives across browser reloads + comes
--     from the matcher (not from re-running the SPA-side classifier
--     on the description, which can't see holdings[]).
--   * Audit trail: distinguishes rows where the matcher abstained
--     (ticker NULL, action probably NULL too) from rows where the
--     matcher recovered the ticker but the user hasn't mapped it
--     yet (ticker NOT NULL, security_id NULL).
--
-- POPULATION: orchestrator writes this on every brokerage txn the
-- provider produces (matches t.SecurityTickerHint from
-- IngestedTransaction). NULL when the matcher / classifier
-- couldn't recover a ticker.
--
-- Also re-runs the resolved_transactions view so the SPA can read
-- the new column.
-- =============================================================================

ALTER TABLE txn_headers
    ADD COLUMN ingest_security_ticker_hint TEXT NULL;

COMMENT ON COLUMN txn_headers.ingest_security_ticker_hint IS 'ADR-0031 Phase 3d follow-up (migration 082): provider-side raw ticker symbol (SimpleFinDescriptionClassifier output OR SimpleFinHoldingsMatcher recovered ticker). Persisted independently of ingest_security_id so the editor typeahead can pre-fill even when the provider_security_mappings lookup didn''t resolve. NULL when the matcher / classifier abstained.';

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
    h.ingest_shares_hint,
    h.ingest_shares_provisional,
    -- ADR-0031 Phase 3d follow-up (migration 082): recovered ticker
    -- text. Used by the editor when the security_id lookup didn't
    -- resolve (first-time-seen ticker with no provider_security_mapping
    -- yet) — pre-fills the typeahead so the user types one char and
    -- sees the right security.
    h.ingest_security_ticker_hint
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
