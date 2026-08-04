-- 167 — register filter: single source of truth (SQL primitive).
--
-- Until now the register filter predicate lived in TWO places: the
-- register_entry_keys SQL function (mig 164/166, for the windowed page) and
-- ApplyRegisterFilterPredicates in RegisterRepository (LINQ, for the date-rail
-- buckets + the status-count badges). Kept in sync by hand — mig 165 existed
-- SOLELY to mirror the 'reconciling' arm into the SQL side, and the copies used
-- subtly different date semantics (SQL posted_at::date vs LINQ UTC-instant).
-- ADR-0076 consolidates the predicate into ONE definition here.
--
-- register_filtered_entries applies the filter (visibility + ledger/account
-- scope + search/date/amount/security/tag/category/status) and returns the
-- matching resolved_transactions rows. Three consumers share it:
--   * register_entry_keys (this migration) selects FROM it, then adds the
--     entry-key GROUP BY + dynamic sort + keyset cursor + LIMIT (page).
--   * RegisterRepository.GetIndexBucketsAsync (rail) and GetStatusCountsAsync
--     (counts) call it via HasDbFunction and aggregate in C#.
-- The LINQ twin (ApplyRegisterFilterPredicates) is deleted.
--
-- Perf: the primitive is a single-SELECT LANGUAGE sql STABLE function, so the
-- planner INLINES it into register_entry_keys' dynamic keyset query. Verified
-- on real data (EXPLAIN identical to the pre-167 inline plan, cost + node tree;
-- no Function Scan barrier, LIMIT selectivity + account index scan preserved).
-- Filtering per-leg then GROUP BY keeps the "entry appears iff ANY leg matches"
-- semantics (entry_key derives from view-precomputed counts, constant per leg).

-- --------------------------------------------------------------------------
-- The shared filter primitive.
-- --------------------------------------------------------------------------
DROP FUNCTION IF EXISTS register_filtered_entries(
    UUID, UUID, BOOLEAN, TEXT, DATE, DATE, NUMERIC, NUMERIC, UUID, TEXT, UUID, TEXT, DATE);

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

-- --------------------------------------------------------------------------
-- register_entry_keys now composes over the primitive. Identical signature +
-- RETURN shape to mig 166 — only the inline filter WHERE is replaced by a call
-- to register_filtered_entries. The dynamic sort + entry-key cursor + keyset
-- (mig 166) are unchanged; the cursor-value derivation still reads the view
-- directly (a point lookup by entry key, not a filter).
-- --------------------------------------------------------------------------
DROP FUNCTION IF EXISTS register_entry_keys(UUID, UUID, UUID, BIGINT, TEXT, INTEGER, BOOLEAN,
    TEXT, DATE, DATE, NUMERIC, NUMERIC, UUID, TEXT, UUID, TEXT, DATE, TEXT, TEXT);

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
