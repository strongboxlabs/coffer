-- =============================================================================
-- 108 — resolved_transactions + register_entry_keys: originating-vs-target
--        register entries (ADR-0036)
-- =============================================================================
--
-- Adds three columns to the view, consumed by RegisterRepository.
-- AssembleEntries and the SPA register surface. Re-shapes the
-- register_entry_keys function so cursor pagination lands on the
-- new entry boundaries.
--
-- New view columns (appended last per CREATE OR REPLACE VIEW
-- column-name-stability rules):
--
--   1. account_postings_on_header — distinct posting_index values
--      of this header that have a leg on THIS row's account.
--      Correlated subquery (Postgres does not support
--      COUNT(DISTINCT …) inside window functions, SQLSTATE 0A000).
--
--   2. header_total_postings — distinct posting_index values across
--      the whole header.
--
--      Equal counts ⇒ the account is the ORIGINATING side of the
--      header (every posting touches it). The user composed the
--      multi-posting event here; the register collapses all legs
--      into one split-parent entry (bank) or one aggregated row
--      (investment).
--
--      Less ⇒ TARGET side. Each posting becomes its own entry;
--      the SPA's split-counter affordance keeps the rows read-only.
--
--   3. derived_action — COALESCE(h.action, 'Xfr' when this leg's
--      counterparty sits on an asset-shaped account, NULL otherwise).
--      True investment events pass through unchanged. Cash-shape
--      headers gain 'Xfr' per leg whose counter is non-category,
--      so target-side per-posting rows render the Action chip
--      correctly in the investment register.
--
-- Function changes: register_entry_keys' entry_key derivation
-- becomes asymmetric to match AssembleEntries.
--
-- security_invoker = true re-asserted per the mig 057 / 077 / 091 /
-- 100 / 107 pattern.
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
    h.seq AS header_seq,
    thab.net_amount AS header_account_net_amount,
    h.provider_key,
    h.is_merge_winner,
    h.import_source,
    -- Mig 108: distinct posting_index values of this header that
    -- have a leg on THIS row's account. Correlated subquery —
    -- Postgres does not support COUNT(DISTINCT …) in a window
    -- (SQLSTATE 0A000).
    (SELECT COUNT(DISTINCT g.posting_index)
       FROM txn_legs g
      WHERE g.header_id = h.id
        AND g.account_id = l.account_id)::int AS account_postings_on_header,
    -- Mig 108: total distinct posting_index values across the
    -- whole header (all accounts).
    (SELECT COUNT(DISTINCT g.posting_index)
       FROM txn_legs g
      WHERE g.header_id = h.id)::int AS header_total_postings,
    -- Mig 108: per-leg derived action. h.action passes through when
    -- set. Otherwise: 'Xfr' only when BOTH sides of this posting
    -- sit on asset-shaped accounts (transfer between two real
    -- accounts). A category leg's counterparty is asset-shape by
    -- construction, but the category leg itself isn't a transfer
    -- — income/expense events on bank accounts must not derive
    -- 'Xfr' just because the category counter is non-category.
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
            ON thab.header_id = h.id AND thab.account_id = l.account_id;

ALTER VIEW resolved_transactions SET (security_invoker = true);

GRANT SELECT ON resolved_transactions TO coffer_app;
GRANT ALL    ON resolved_transactions TO coffer_service;

-- -----------------------------------------------------------------------------
-- register_entry_keys: asymmetric entry-key derivation (ADR-0036).
--
-- Same pagination structure as mig 097 — but the entry_key now
-- switches on the originating-vs-target distinction:
--
--   * `account_postings_on_header = header_total_postings`
--     (originating): entry_key = header_id. All legs of this header
--     on this account cluster into ONE entry — bank's split-parent
--     / investment aggregator collapse target.
--
--   * `account_postings_on_header < header_total_postings`
--     (target): entry_key = leg id. Each posting touching this
--     account becomes its OWN entry; the SPA's split-counter
--     affordance keeps them read-only via TxnGroupId != null.
--
-- Drop-and-recreate per the project's pattern (signature unchanged).
-- -----------------------------------------------------------------------------

DROP FUNCTION register_entry_keys(UUID, UUID, TIMESTAMPTZ, BIGINT, TEXT, INTEGER);

CREATE FUNCTION register_entry_keys(
    p_account_id        UUID,
    p_ledger_id         UUID,
    p_cursor_posted_at  TIMESTAMPTZ,
    p_cursor_seq        BIGINT,
    p_direction         TEXT,
    p_limit             INTEGER
)
RETURNS TABLE(
    posted_at  TIMESTAMPTZ,
    seq        BIGINT,
    entry_key  UUID
)
LANGUAGE SQL
STABLE
PARALLEL SAFE
AS $$
    SELECT posted_at, seq, entry_key FROM (
        SELECT
            MAX(rt.posted_at)  AS posted_at,
            MAX(rt.header_seq) AS seq,
            CASE
                WHEN rt.account_postings_on_header < rt.header_total_postings
                    THEN rt.id           -- target: per-leg entry
                ELSE rt.header_id        -- originating: per-header entry
            END AS entry_key
        FROM resolved_transactions rt
        WHERE rt.is_hidden = FALSE
          AND rt.is_merged_into IS NULL
          AND (p_account_id IS NULL OR rt.account_id = p_account_id)
          AND (
              p_account_id IS NOT NULL
              OR EXISTS (
                  SELECT 1 FROM accounts a
                  WHERE a.id = rt.account_id AND a.ledger_id = p_ledger_id
              )
          )
        GROUP BY
            CASE
                WHEN rt.account_postings_on_header < rt.header_total_postings
                    THEN rt.id
                ELSE rt.header_id
            END
        HAVING
            p_cursor_posted_at IS NULL
            OR (p_direction = 'before' AND (
                MAX(rt.posted_at) < p_cursor_posted_at
                OR (MAX(rt.posted_at) = p_cursor_posted_at
                    AND MAX(rt.header_seq) < p_cursor_seq)
            ))
            OR (p_direction = 'after' AND (
                MAX(rt.posted_at) > p_cursor_posted_at
                OR (MAX(rt.posted_at) = p_cursor_posted_at
                    AND MAX(rt.header_seq) > p_cursor_seq)
            ))
        ORDER BY
            CASE WHEN p_direction = 'after' THEN MAX(rt.posted_at) END ASC,
            CASE WHEN p_direction = 'after' THEN MAX(rt.header_seq) END ASC,
            MAX(rt.posted_at) DESC,
            MAX(rt.header_seq) DESC
        LIMIT p_limit
    ) sub
    ORDER BY posted_at DESC, seq DESC;
$$;

GRANT EXECUTE ON FUNCTION
    register_entry_keys(UUID, UUID, TIMESTAMPTZ, BIGINT, TEXT, INTEGER)
    TO coffer_app;
