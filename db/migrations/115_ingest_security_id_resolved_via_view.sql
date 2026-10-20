-- =============================================================================
-- 115 — `ingest_security_id` resolved dynamically via view JOIN (ADR-0038)
-- =============================================================================
--
-- Retires the denormalized `txn_headers.ingest_security_id` column
-- (mig 076) and the repo-layer backfill (PR #166 follow-up) that
-- kept it in sync with `provider_security_mappings`. The resolved
-- view now LEFT JOINs `provider_security_mappings` on
-- (ledger_id, provider_key, ingest_security_ticker_hint) and
-- projects the matched `security_id` as `ingest_security_id` per
-- leg. The mapping table becomes the single source of truth; re-
-- link propagates instantly with no backfill.
--
-- Rationale + alternatives: ADR-0038.
--
-- Order:
--   1. DROP the view (Postgres won't drop a column the view
--      references).
--   2. DROP the column (cascades the composite FK to securities
--      from mig 076 and the partial index
--      `idx_txn_headers_ingest_security_id`).
--   3. RECREATE the view with the LEFT JOIN on
--      `provider_security_mappings`.
-- =============================================================================

DROP VIEW IF EXISTS resolved_transactions;

ALTER TABLE txn_headers
    DROP COLUMN ingest_security_id;

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
    h.needs_review,
    COALESCE(l.security_id, other.security_id) AS security_id,
    s.ticker AS security_ticker,
    s.name AS security_name,
    COALESCE(l.quantity, other.quantity) AS quantity,
    COALESCE(l.unit_price, other.unit_price) AS unit_price,
    l.posting_role,
    h.ingest_action_hint,
    -- ADR-0038: resolved dynamically from provider_security_mappings
    -- via the LEFT JOIN below (on the header's ledger / provider /
    -- ticker-hint triple). NULL when no mapping exists yet for this
    -- ticker — the editor's Accept flow records one and every row
    -- of the same ticker immediately sees the resolved security on
    -- the next read.
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
    (SELECT COUNT(DISTINCT g.posting_index)
       FROM txn_legs g
      WHERE g.header_id = h.id
        AND g.account_id = l.account_id)::int AS account_postings_on_header,
    (SELECT COUNT(DISTINCT g.posting_index)
       FROM txn_legs g
      WHERE g.header_id = h.id)::int AS header_total_postings,
    COALESCE(
        h.action,
        CASE
            WHEN this_account.account_type <> 'category'
                AND ca.account_type IS NOT NULL
                AND ca.account_type <> 'category'
                THEN 'Xfr'
            ELSE NULL
        END
    ) AS derived_action
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
