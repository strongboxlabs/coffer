-- =============================================================================
-- 226 — the register can scope to a SET of accounts, not just one.
-- =============================================================================
--
-- WHY. A parent category's register is empty. All of the money is on its
-- children, so opening "Taxes" shows nothing while every other screen says it
-- holds five figures. That is not a budget-screen problem: it is true from the
-- Categories tree, from the sidebar, from anywhere that links a category to its
-- register, and it has been true since categories got registers.
--
-- The fix is to let the register scope to a category AND its descendants, which
-- means the one thing the two functions could not express: more than one
-- account. `p_account_id uuid` becomes `p_account_ids uuid[]`, and the single
-- account case passes an array of one.
--
-- WHY A TYPE CHANGE RATHER THAN AN EXTRA PARAMETER. An added
-- `p_account_ids uuid[] DEFAULT NULL` alongside the existing scalar would leave
-- two ways to say the same thing and a silent precedence question between them
-- whenever both are supplied. It also cannot be done with CREATE OR REPLACE:
-- changing the parameter list creates an OVERLOAD, and every existing call
-- becomes ambiguous rather than failing loudly. Both functions are therefore
-- DROPped by full signature and recreated, which is the pattern migration 171
-- already established for this pair.
--
-- WHAT DID NOT CHANGE. The predicate was `rt.account_id = $1` and is now
-- `rt.account_id = ANY($1)`; every other use of that parameter is an IS NULL or
-- IS NOT NULL test, which reads identically on an array. The keyset cursor, the
-- sort whitelist, the visibility fallback and all of the filters are untouched
-- — the bodies below are the LIVE definitions with those single lines changed,
-- extracted with pg_get_functiondef rather than copied from an older migration
-- so that nothing silently reverts (migration 205 did exactly that once, and
-- 209 exists only to undo it).
--
--     SELECT pg_get_functiondef('register_entry_keys'::regproc);
--
-- PERFORMANCE. `= ANY(array)` on a one-element array plans the same as `=` for
-- the index on (account_id, ...); Postgres treats a single-element ANY as an
-- equality. The subtree case is a small IN-list — a category tree is tens of
-- rows, not thousands — so this does not turn the register's hottest query into
-- a scan.
-- =============================================================================

BEGIN;

-- Full signatures, because an overload is exactly what must not survive.
DROP FUNCTION IF EXISTS register_entry_keys(
    UUID, UUID, UUID, BIGINT, TEXT, INTEGER, BOOLEAN, TEXT, DATE, DATE,
    NUMERIC, NUMERIC, UUID, TEXT, UUID, TEXT, DATE, TEXT, TEXT);

DROP FUNCTION IF EXISTS register_filtered_entries(
    UUID, UUID, BOOLEAN, TEXT, DATE, DATE, NUMERIC, NUMERIC, UUID, TEXT,
    UUID, TEXT, DATE);

CREATE OR REPLACE FUNCTION public.register_entry_keys(p_account_ids uuid[], p_ledger_id uuid, p_cursor_entry_key uuid, p_cursor_seq bigint, p_direction text, p_limit integer, p_hidden boolean DEFAULT false, p_search text DEFAULT NULL::text, p_date_from date DEFAULT NULL::date, p_date_to date DEFAULT NULL::date, p_amount_min numeric DEFAULT NULL::numeric, p_amount_max numeric DEFAULT NULL::numeric, p_security_id uuid DEFAULT NULL::uuid, p_tag text DEFAULT NULL::text, p_category_id uuid DEFAULT NULL::uuid, p_status text DEFAULT NULL::text, p_today date DEFAULT NULL::date, p_sort_column text DEFAULT 'date'::text, p_sort_dir text DEFAULT 'desc'::text)
 RETURNS TABLE(posted_at timestamp with time zone, seq bigint, entry_key uuid)
 LANGUAGE plpgsql
 STABLE PARALLEL SAFE
AS $function$
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
        'AND ($1 IS NULL OR rt.account_id = ANY($1)) '
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
    USING p_account_ids,       -- $1
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
$function$
;

CREATE OR REPLACE FUNCTION public.register_filtered_entries(p_account_ids uuid[], p_ledger_id uuid, p_hidden boolean, p_search text DEFAULT NULL::text, p_date_from date DEFAULT NULL::date, p_date_to date DEFAULT NULL::date, p_amount_min numeric DEFAULT NULL::numeric, p_amount_max numeric DEFAULT NULL::numeric, p_security_id uuid DEFAULT NULL::uuid, p_tag text DEFAULT NULL::text, p_category_id uuid DEFAULT NULL::uuid, p_status text DEFAULT NULL::text, p_today date DEFAULT NULL::date)
 RETURNS SETOF resolved_transactions
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
    SELECT rt.* FROM resolved_transactions rt
    -- p_hidden: TRUE/FALSE selects one visibility side (page/rail/counts);
    -- NULL returns both (select-all, whose own query already scopes visibility).
    WHERE (p_hidden IS NULL OR rt.is_hidden = p_hidden)
      AND rt.is_merged_into IS NULL
      AND (p_account_ids IS NULL OR rt.account_id = ANY(p_account_ids))
      AND (p_account_ids IS NOT NULL
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
$function$
;

COMMIT;
