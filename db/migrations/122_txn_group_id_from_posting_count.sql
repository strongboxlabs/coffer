-- =============================================================================
-- 122 — txn_group_id reads denormalized posting count (ADR-0046 read-path cleanup)
-- =============================================================================
--
-- WHY
--
-- Mig 120 denormalized `header_total_postings` onto txn_legs and rewired
-- `resolved_transactions` to read it instead of running two correlated
-- COUNT(DISTINCT) subqueries. But the view STILL computed `txn_group_id`
-- via a separate per-row correlated EXISTS subquery:
--
--   CASE WHEN (EXISTS (SELECT 1 FROM txn_legs g
--                       WHERE g.header_id = h.id AND g.posting_index > 0))
--        THEN h.id ELSE NULL::uuid END
--
-- That EXISTS asks "does this header have any leg with posting_index > 0?"
-- — i.e. "is this a multi-posting (grouped) transaction?". With 0-based
-- contiguous posting indices per ADR-0019, a leg with posting_index > 0
-- exists EXACTLY WHEN COUNT(DISTINCT posting_index) > 1. And
-- `header_total_postings` IS that count (maintained by
-- fn_recompute_posting_counts_for_header via the recompute interceptor).
--
-- So the EXISTS is provably equivalent to `l.header_total_postings > 1`,
-- a column read we already pay for. This kills the last per-row
-- correlated subquery in the view for free — no new columns, no new
-- maintenance path, reusing mig 120's denormalized state.
--
-- CREATE OR REPLACE — column shape / order / types are byte-identical to
-- mig 120; the ONLY change is the txn_group_id expression. The view body
-- below is copied verbatim from mig 120 with that single substitution.
-- =============================================================================

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
    thab.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    h.action AS investment_action,
    h.external_id,
    l.created_at,
    COALESCE(o.check_number, h.check_number) AS check_number,
    other.id AS counterparty_id,
        CASE
            WHEN l.header_total_postings > 1 THEN h.id
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
    h.needs_review,
    COALESCE(l.security_id, other.security_id) AS security_id,
    s.ticker AS security_ticker,
    s.name AS security_name,
    COALESCE(l.quantity, other.quantity) AS quantity,
    COALESCE(l.unit_price, other.unit_price) AS unit_price,
    l.posting_role,
    h.ingest_action_hint,
    psm.security_id AS ingest_security_id,
    h.ingest_shares,
    h.ingest_unit_price,
    h.ingest_fee,
    h.ingest_security_ticker_hint,
    h.provider_raw_payload,
    h.seq AS header_seq,
    thab.net_amount AS header_account_net_amount,
    h.provider_key,
    h.is_merge_winner,
    h.import_source,
    -- Mig 120: denormalized — read straight off the leg instead of two
    -- per-row correlated COUNT(DISTINCT) subqueries (maintained by
    -- fn_recompute_posting_counts_for_header via the recompute interceptor).
    l.account_postings_on_header,
    l.header_total_postings,
    COALESCE(
        h.action,
        CASE
            WHEN this_account.account_type <> 'category'
                AND ca.account_type IS NOT NULL
                AND ca.account_type <> 'category'
                THEN 'Xfr'
            ELSE NULL
        END
    ) AS derived_action,
    this_account.account_type AS account_type
   FROM txn_legs l
     JOIN txn_headers h ON h.id = l.header_id
     JOIN accounts this_account ON this_account.id = l.account_id
     LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
     LEFT JOIN txn_legs other ON other.header_id = l.header_id AND other.posting_index = l.posting_index AND other.id <> l.id
     LEFT JOIN accounts ca ON ca.id = other.account_id
     LEFT JOIN securities s ON s.id = COALESCE(l.security_id, other.security_id)
     LEFT JOIN txn_header_account_balances thab
            ON thab.header_id = h.id AND thab.account_id = l.account_id
     LEFT JOIN provider_security_mappings psm
            ON psm.ledger_id = h.ledger_id
           AND psm.provider_key = h.provider_key
           AND psm.provider_security_id = h.ingest_security_ticker_hint;

ALTER VIEW resolved_transactions SET (security_invoker = true);

GRANT SELECT ON resolved_transactions TO coffer_app;
GRANT ALL    ON resolved_transactions TO coffer_service;
