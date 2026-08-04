-- 171 — per-account reconciliation status (ADR-0082)
--
-- Reconciliation status was header-level (txn_headers.status + cleared_at +
-- cleared_by_user_id, migration 030). But reconciliation is a per-ACCOUNT
-- activity — a transfer from Checking to Savings can be cleared in Checking
-- while still uncleared in Savings — so one header-level status is wrong.
--
-- Status moves to a per-LEG overlay keyed by leg_id (ADR-0082, Option B). Only
-- real-account legs are ever reconciled; category legs never get a row and
-- resolve to 'uncleared'. ADR-0003: the raw feed (txn_legs) stays immutable —
-- the user's clearing action lives in the overlay, mirroring txn_leg_overrides.

-- ---------------------------------------------------------------------------
-- 1. Overlay table (mirrors the txn_leg_overrides RLS + composite-FK shape,
--    migrations 022 + 072).
-- ---------------------------------------------------------------------------
CREATE TABLE txn_leg_recon (
    leg_id             UUID           PRIMARY KEY REFERENCES txn_legs(id) ON DELETE CASCADE,
    ledger_id          UUID           NOT NULL,
    status             TEXT           NOT NULL DEFAULT 'uncleared'
                                          CHECK (status IN ('uncleared', 'reconciling', 'cleared')),
    cleared_at         TIMESTAMPTZ,
    cleared_by_user_id UUID           REFERENCES users(id) ON DELETE SET NULL,
    -- Same consistency invariant migration 030 put on the header: a row is
    -- 'cleared' iff it carries a cleared_at timestamp.
    CONSTRAINT txn_leg_recon_cleared_consistency
        CHECK ((status = 'cleared') = (cleared_at IS NOT NULL)),
    CONSTRAINT txn_leg_recon_leg_ledger_fkey
        FOREIGN KEY (leg_id, ledger_id) REFERENCES txn_legs(id, ledger_id) ON DELETE CASCADE
);

CREATE INDEX idx_txn_leg_recon_ledger ON txn_leg_recon (ledger_id);

ALTER TABLE txn_leg_recon ENABLE ROW LEVEL SECURITY;
CREATE POLICY txn_leg_recon_per_user ON txn_leg_recon
    FOR ALL TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                              WHERE ulg.user_id = current_app_user_id()));

GRANT SELECT, INSERT, UPDATE, DELETE ON txn_leg_recon TO coffer_app;
GRANT ALL ON txn_leg_recon TO coffer_service;

-- ---------------------------------------------------------------------------
-- 2. Backfill: fan the header status out to each REAL-ACCOUNT leg. Only rows
--    that carry non-default state need an overlay row (absent ⇒ 'uncleared').
--    Category legs are skipped (never reconciled). cleared_at / cleared_by fan
--    out verbatim; the pre-per-account world had every leg equal to the header.
-- ---------------------------------------------------------------------------
INSERT INTO txn_leg_recon (leg_id, ledger_id, status, cleared_at, cleared_by_user_id)
SELECT l.id, l.ledger_id, h.status, h.cleared_at, h.cleared_by_user_id
  FROM txn_legs l
  JOIN txn_headers h ON h.id = l.header_id
  JOIN accounts a ON a.id = l.account_id
 WHERE h.status <> 'uncleared'
   AND a.account_type <> 'category';

-- ---------------------------------------------------------------------------
-- 3. Re-source status / cleared_at / cleared_by in resolved_transactions from
--    the per-leg overlay. Each resolved row is already leg-scoped (FROM
--    txn_legs l), so the LEFT JOIN on leg_id yields per-account status — a
--    transfer becomes two rows with independent statuses. Verbatim from
--    migration 124 except the three status columns + the new join.
--
--    Ordering note: live_txn_headers / template_txn_headers are SELECT * on
--    txn_headers (mig 124), so their expanded column lists pin status /
--    cleared_at / cleared_by_user_id and would BLOCK the column drop below.
--    resolved_transactions in turn depends on live_txn_headers. So drop all
--    three, drop the header columns, then recreate: the SELECT* pair first,
--    resolved_transactions (with the per-leg re-source) last.
-- ---------------------------------------------------------------------------
-- Drop the register keyset functions first (mig 167): they depend on
-- resolved_transactions, so the view can't be dropped until they're gone. They
-- are recreated verbatim in section 4 once the view is rebuilt — their bodies
-- read resolved_transactions.status (now overlay-sourced), so no body change.
DROP FUNCTION IF EXISTS register_entry_keys(UUID, UUID, UUID, BIGINT, TEXT, INTEGER, BOOLEAN,
    TEXT, DATE, DATE, NUMERIC, NUMERIC, UUID, TEXT, UUID, TEXT, DATE, TEXT, TEXT);
DROP FUNCTION IF EXISTS register_filtered_entries(
    UUID, UUID, BOOLEAN, TEXT, DATE, DATE, NUMERIC, NUMERIC, UUID, TEXT, UUID, TEXT, DATE);

DROP VIEW resolved_transactions;
DROP VIEW live_txn_headers;
DROP VIEW template_txn_headers;

ALTER TABLE txn_headers
    DROP COLUMN status,
    DROP COLUMN cleared_at,
    DROP COLUMN cleared_by_user_id;

-- Recreate the SELECT* header views (now without the dropped columns).
CREATE VIEW live_txn_headers     AS SELECT * FROM txn_headers WHERE NOT is_recurring_template;
CREATE VIEW template_txn_headers AS SELECT * FROM txn_headers WHERE     is_recurring_template;
ALTER VIEW live_txn_headers     SET (security_invoker = true);
ALTER VIEW template_txn_headers SET (security_invoker = true);
GRANT SELECT ON live_txn_headers, template_txn_headers TO coffer_app;
GRANT ALL    ON live_txn_headers, template_txn_headers TO coffer_service;

CREATE VIEW resolved_transactions AS
SELECT l.id,
    l.account_id,
    COALESCE(o.payee, h.payee) AS payee,
    COALESCE(lo.leg_memo, l.leg_memo, o.memo, h.memo) AS memo,
    COALESCE(lo.amount, l.amount) AS amount,
    COALESCE(o.posted_at, h.posted_at) AS posted_at,
    COALESCE(o.transacted_at, h.transacted_at) AS transacted_at,
    COALESCE(lr.status, 'uncleared') AS status,
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
    lr.cleared_at,
    lr.cleared_by_user_id,
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
     JOIN live_txn_headers h ON h.id = l.header_id
     JOIN accounts this_account ON this_account.id = l.account_id
     LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
     LEFT JOIN txn_leg_recon lr ON lr.leg_id = l.id
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
-- (The header status columns were dropped above, between the view drop and
-- recreate — see the ordering note; their two CHECK constraints + the
-- cleared_by_user_id FK are column-dependent and dropped with them.)

-- ---------------------------------------------------------------------------
-- 4. Recreate the register keyset functions (verbatim from mig 167). Their
--    bodies are UNCHANGED — they read resolved_transactions.status, which is now
--    overlay-sourced. They were dropped in section 3 (they depend on the view);
--    recreated here now the view exists again. (Copied byte-for-byte from
--    167_register_filtered_entries.sql; kept in lockstep if that migration ever
--    changes.)
-- ---------------------------------------------------------------------------
CREATE FUNCTION register_filtered_entries(
    p_account_id  UUID,
    p_ledger_id   UUID,
    p_hidden      BOOLEAN,
    p_search      TEXT DEFAULT NULL,
    p_date_from   DATE DEFAULT NULL,
    p_date_to     DATE DEFAULT NULL,
    p_amount_min  NUMERIC DEFAULT NULL,
    p_amount_max  NUMERIC DEFAULT NULL,
    p_security_id UUID DEFAULT NULL,
    p_tag         TEXT DEFAULT NULL,
    p_category_id UUID DEFAULT NULL,
    p_status      TEXT DEFAULT NULL,
    p_today       DATE DEFAULT NULL
)
RETURNS SETOF resolved_transactions
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $fe$
    SELECT rt.* FROM resolved_transactions rt
    -- p_hidden: TRUE/FALSE selects one visibility side (page/rail/counts);
    -- NULL returns both (select-all, whose own query already scopes visibility).
    WHERE (p_hidden IS NULL OR rt.is_hidden = p_hidden)
      AND rt.is_merged_into IS NULL
      AND (p_account_id IS NULL OR rt.account_id = p_account_id)
      AND (p_account_id IS NOT NULL
           OR EXISTS (SELECT 1 FROM accounts a
                      WHERE a.id = rt.account_id AND a.ledger_id = p_ledger_id))
      -- Filters (each a no-op when its arg is NULL). This is the ONE place
      -- these predicates live; the date comparison is calendar-date
      -- (posted_at::date), authoritative for the page, rail, and counts alike.
      AND (p_date_from IS NULL OR rt.posted_at::date >= p_date_from)
      AND (p_date_to   IS NULL OR rt.posted_at::date <= p_date_to)
      AND (p_amount_min IS NULL
           OR ABS(COALESCE(rt.header_account_net_amount, rt.amount)) >= p_amount_min)
      AND (p_amount_max IS NULL
           OR ABS(COALESCE(rt.header_account_net_amount, rt.amount)) <= p_amount_max)
      AND (p_security_id IS NULL OR rt.security_id = p_security_id)
      AND (p_category_id IS NULL OR rt.counterparty_account_id = p_category_id)
      AND (p_tag IS NULL OR p_tag = ANY(rt.tags))
      AND (p_search IS NULL OR (
              rt.payee ILIKE '%' || p_search || '%'
           OR rt.memo ILIKE '%' || p_search || '%'
           OR rt.check_number ILIKE '%' || p_search || '%'
           OR rt.counterparty_account_name ILIKE '%' || p_search || '%'
           OR EXISTS (SELECT 1 FROM unnest(rt.tags) tg WHERE tg ILIKE '%' || p_search || '%')))
      AND (
              p_status IS NULL
           OR (p_status = 'needs_review' AND rt.needs_review = TRUE)
           OR (p_status = 'scheduled'
                 AND rt.posted_at::date > COALESCE(p_today, CURRENT_DATE))
           OR (p_status = 'cleared'
                 AND rt.status = 'cleared'     AND rt.is_pending = FALSE
                 AND rt.posted_at::date <= COALESCE(p_today, CURRENT_DATE))
           OR (p_status = 'uncleared'
                 AND rt.status = 'uncleared'   AND rt.is_pending = FALSE
                 AND rt.posted_at::date <= COALESCE(p_today, CURRENT_DATE))
           OR (p_status = 'reconciling'
                 AND rt.status = 'reconciling' AND rt.is_pending = FALSE
                 AND rt.posted_at::date <= COALESCE(p_today, CURRENT_DATE)));
$fe$;

GRANT EXECUTE ON FUNCTION register_filtered_entries(
    UUID, UUID, BOOLEAN, TEXT, DATE, DATE, NUMERIC, NUMERIC, UUID, TEXT, UUID, TEXT, DATE)
    TO coffer_app;
CREATE FUNCTION register_entry_keys(
    p_account_id        UUID,
    p_ledger_id         UUID,
    p_cursor_entry_key  UUID,
    p_cursor_seq        BIGINT,
    p_direction         TEXT,
    p_limit             INTEGER,
    p_hidden            BOOLEAN DEFAULT FALSE,
    p_search            TEXT    DEFAULT NULL,
    p_date_from         DATE    DEFAULT NULL,
    p_date_to           DATE    DEFAULT NULL,
    p_amount_min        NUMERIC DEFAULT NULL,
    p_amount_max        NUMERIC DEFAULT NULL,
    p_security_id       UUID    DEFAULT NULL,
    p_tag               TEXT    DEFAULT NULL,
    p_category_id       UUID    DEFAULT NULL,
    p_status            TEXT    DEFAULT NULL,
    p_today             DATE    DEFAULT NULL,
    p_sort_column       TEXT    DEFAULT 'date',
    p_sort_dir          TEXT    DEFAULT 'desc'
)
RETURNS TABLE(
    posted_at  TIMESTAMPTZ,
    seq        BIGINT,
    entry_key  UUID
)
LANGUAGE plpgsql
STABLE
PARALLEL SAFE
AS $func$
DECLARE
    -- ADR-0036 asymmetric entry key: leg id for a target-split entry (the
    -- account touches fewer legs than the header has), else the header id.
    v_entry_key CONSTANT TEXT :=
        'CASE WHEN rt.account_postings_on_header < rt.header_total_postings '
        'THEN rt.id ELSE rt.header_id END';

    v_sort_expr TEXT;   -- the entry's sort value: a coalesced MAX() aggregate
    v_dir       TEXT;   -- 'ASC' | 'DESC' — the display direction
    v_fetch_dir TEXT;   -- 'ASC' | 'DESC' — inner fetch dir (reversed for 'after')
    v_op        TEXT;   -- '<' | '>'      — keyset comparison operator
    v_cursor_val TEXT;  -- SQL deriving the cursor entry's sort value from its key
BEGIN
    -- Whitelist: sort column → aggregate expression. Unknown → 'date'. Values
    -- are coalesced non-null so the keyset stays a plain row comparison.
    v_sort_expr := CASE p_sort_column
        WHEN 'amount'   THEN 'MAX(COALESCE(rt.header_account_net_amount, rt.amount))'
        WHEN 'payee'    THEN 'COALESCE(MAX(rt.payee), '''')'
        WHEN 'category' THEN 'COALESCE(MAX(rt.counterparty_account_name), '''')'
        WHEN 'security' THEN 'COALESCE(MAX(rt.security_ticker), '''')'
        WHEN 'shares'   THEN 'COALESCE(MAX(rt.quantity), 0)'
        WHEN 'price'    THEN 'COALESCE(MAX(rt.unit_price), 0)'
        WHEN 'action'   THEN 'COALESCE(MAX(rt.derived_action), '''')'
        ELSE                 'MAX(rt.posted_at)'   -- 'date' (default)
    END;

    v_dir := CASE WHEN lower(p_sort_dir) = 'asc' THEN 'ASC' ELSE 'DESC' END;

    -- p_direction='before' = the next page in display order (scroll down);
    -- 'after' = the previous page (scroll up), fetched in reverse then
    -- re-sorted to display order by the outer query. The keyset operator walks
    -- AWAY from the cursor in the requested direction:
    --   display DESC → 'before' uses '<' (older/smaller), 'after' uses '>'
    --   display ASC  → 'before' uses '>' (larger),        'after' uses '<'
    IF p_direction = 'after' THEN
        v_fetch_dir := CASE WHEN v_dir = 'ASC' THEN 'DESC' ELSE 'ASC' END;
        v_op        := CASE WHEN v_dir = 'ASC' THEN '<' ELSE '>' END;
    ELSE
        v_fetch_dir := v_dir;
        v_op        := CASE WHEN v_dir = 'ASC' THEN '>' ELSE '<' END;
    END IF;

    -- The cursor entry's sort value, derived from its key ($16). The OR-match
    -- resolves to exactly the cursor entry's legs (header-id match for a normal
    -- entry, leg-id match for a target split), so the implicit aggregate yields
    -- a single scalar. Same visibility/scope predicate as the primitive; this
    -- is a point lookup by key, not a filter, so it reads the view directly.
    v_cursor_val := format(
        '(SELECT %s FROM resolved_transactions rt '
        'WHERE (rt.header_id = $16 OR rt.id = $16) '
        'AND rt.is_hidden = $3 AND rt.is_merged_into IS NULL '
        'AND ($1 IS NULL OR rt.account_id = $1) '
        'AND ($1 IS NOT NULL OR EXISTS ('
        'SELECT 1 FROM accounts a WHERE a.id = rt.account_id AND a.ledger_id = $2)))',
        v_sort_expr);

    RETURN QUERY EXECUTE format($q$
        SELECT posted_at, seq, entry_key FROM (
            SELECT
                MAX(rt.posted_at)  AS posted_at,
                MAX(rt.header_seq) AS seq,
                %1$s               AS entry_key,
                %2$s               AS sort_val
            -- Single source of truth for the filter (mig 167). Inlines into
            -- this keyset query — no plan change vs the pre-167 inline WHERE.
            FROM register_filtered_entries($1, $2, $3, $11, $4, $5, $6, $7, $8, $10, $9, $12, $13) rt
            GROUP BY %1$s
            -- Keyset: entries strictly past the cursor in the fetch direction.
            -- Non-null sort values ⇒ a plain 3-tuple row comparison; entry_key
            -- is the final tiebreaker so the order is total.
            HAVING $16 IS NULL
               OR (%2$s, MAX(rt.header_seq), %1$s) %3$s (%4$s, $15, $16)
            ORDER BY sort_val %5$s, seq %5$s, entry_key %5$s
            LIMIT $14
        ) sub
        ORDER BY sort_val %6$s, seq %6$s, entry_key %6$s
    $q$, v_entry_key, v_sort_expr, v_op, v_cursor_val, v_fetch_dir, v_dir)
    USING p_account_id,        -- $1
          p_ledger_id,         -- $2
          p_hidden,            -- $3
          p_date_from,         -- $4
          p_date_to,           -- $5
          p_amount_min,        -- $6
          p_amount_max,        -- $7
          p_security_id,       -- $8
          p_category_id,       -- $9
          p_tag,               -- $10
          p_search,            -- $11
          p_status,            -- $12
          p_today,             -- $13
          p_limit,             -- $14
          p_cursor_seq,        -- $15
          p_cursor_entry_key;  -- $16
END;
$func$;

GRANT EXECUTE ON FUNCTION
    register_entry_keys(UUID, UUID, UUID, BIGINT, TEXT, INTEGER, BOOLEAN,
        TEXT, DATE, DATE, NUMERIC, NUMERIC, UUID, TEXT, UUID, TEXT, DATE, TEXT, TEXT)
    TO coffer_app;
