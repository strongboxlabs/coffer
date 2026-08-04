-- =============================================================================
-- 097 — resolved_transactions + register_entry_keys use (posted_at, seq)
--        and entry_key = h.id consistently (ADR-0034 v2)
-- =============================================================================
--
-- View change: project `h.seq AS header_seq` so consumers (the API's
-- register cursor codec, future read paths) can sort by the canonical
-- pair without an extra JOIN.
--
-- register_entry_keys change: drop the `(created_at, COALESCE(...,id))`
-- tiebreaker pair in favor of seq. Also drop the COALESCE — entry_key
-- is ALWAYS h.id now, even for single-posting events. The grouping
-- key matches what the trigger sorts by, eliminating the
-- read/write-order disagreement at the root.
--
-- Signature change: cursor parameters shrink from (posted_at, created_at,
-- entry_key) to (posted_at, seq). Drop-and-recreate per the project's
-- pattern (mig 029, mig 031). The EF HasDbFunction binding follows in
-- the same PR.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1) resolved_transactions: expose header_seq.
-- -----------------------------------------------------------------------------

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
    -- ADR-0034 v2: canonical sort key tiebreaker. Projected so the
    -- register's grouped query can ORDER BY MAX(header_seq) without
    -- a second JOIN, and so the cursor codec can carry seq directly.
    h.seq AS header_seq
   FROM txn_legs l
     JOIN txn_headers h ON h.id = l.header_id
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
-- 2) register_entry_keys: (posted_at, seq) cursor, h.id entry_key.
-- -----------------------------------------------------------------------------

DROP FUNCTION register_entry_keys(UUID, UUID, TIMESTAMPTZ, TIMESTAMPTZ, UUID, TEXT, INTEGER);

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
    -- Same bidirectional pagination structure as mig 031: inner query
    -- picks K entries adjacent to the cursor in the chosen direction,
    -- outer SELECT re-sorts to uniform time-DESC for the consumer.
    SELECT posted_at, seq, entry_key FROM (
        SELECT
            MAX(rt.posted_at)  AS posted_at,
            MAX(rt.header_seq) AS seq,
            rt.header_id       AS entry_key
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
        GROUP BY rt.header_id
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
