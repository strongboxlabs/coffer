-- =============================================================================
-- 119 — expose `account_type` on resolved_transactions (ADR-0030 §2)
-- =============================================================================
--
-- The register read surface is moving from a single bag-of-nullable-
-- per-domain-fields DTO to a `kind`-discriminated union
-- (`BankRow | InvestmentRow`). The discriminant is the ACCOUNT's domain,
-- not a per-leg signal: an investment register renders every one of its
-- rows with investment chrome — including cash deposits and fee rows
-- whose leg carries no security — so `kind` must follow the row's
-- account type, not whether that particular leg touches a security.
--
-- The view already JOINs `accounts this_account ON this_account.id =
-- l.account_id`, so the only change is projecting that join's
-- `account_type` as a new trailing column. The repository's projection
-- branches on it: 'investment' -> InvestmentRow, everything else
-- (bank / credit_card / cash / asset / liability / category) -> BankRow.
--
-- Additive, column appended LAST so CREATE OR REPLACE VIEW succeeds
-- without a DROP (no dependent-object risk). No data migration; the
-- column is computed from an already-present join.
--
-- Rationale + the full contract spec: ADR-0030 §2.
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
    ) AS derived_action,
    -- Mig 119 (ADR-0030 §2): the register-row discriminant. Projected
    -- from the already-joined owning account. The repository branches
    -- 'investment' -> InvestmentRow, everything else -> BankRow.
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
