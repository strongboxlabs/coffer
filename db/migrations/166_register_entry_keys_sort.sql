-- 166 — parameterize register_entry_keys with a sort column + direction.
--
-- mig 164/165 fixed the register ordering at (posted_at DESC, header_seq DESC).
-- The register UI now offers column sorting: Date / Amount / Payee / Category on
-- every register, plus Security / Shares / Price / Action on investment
-- registers. Sort is a DISPLAY-ORDER concern only — counts and select-all are
-- order-independent — so this touches ONLY the windowed read function; the LINQ
-- twin (ApplyRegisterFilterPredicates) and the counts / rail / select-all paths
-- are unchanged.
--
-- Design
-- ------
--   * Two new params: p_sort_column (whitelisted → a per-entry MAX() aggregate)
--     and p_sort_dir ('asc'/'desc'). The whitelist CASE is the ONLY place these
--     reach SQL — an unknown column falls back to the date expression and an
--     unknown direction to 'desc', so neither value is ever concatenated raw
--     (no injection surface). Every data/filter value is still bound via USING.
--
--   * The cursor is now ENTRY-KEY based. The old (posted_at) cursor slot at
--     position 3 becomes p_cursor_entry_key, and the function derives the cursor
--     entry's sort value internally from that key (one bounded, header-id-indexed
--     subquery). This means the API's opaque cursor never has to carry the typed
--     sort value: the wire format AND this function's RETURN shape are both
--     unchanged, and the starting_at (anchor-on-header) path needs no change —
--     it already resolves a header to its entry key.
--
--     Trade-off (documented, deliberate): deriving the cursor value from the
--     entry key requires that entry to still exist. If another session deletes
--     the exact page-boundary entry between two scroll fetches, that page comes
--     back empty until the next refetch. This is rare and self-healing — every
--     register mutation invalidates + refetches the register, rebuilding cursors
--     — so the window is tiny. The alternative (encoding the typed sort value in
--     the cursor) buys nothing here for the cost of a wider wire contract.
--
--   * Sort values are COALESCEd to non-null ('' for text, 0 for numeric; date is
--     never null), so the keyset comparison stays a plain 3-tuple row comparison
--     with no NULLS-LAST special-casing. Rows lacking the dimension (e.g. a
--     brokerage cash row has no shares) collapse to the '' / 0 end consistently
--     in both directions.
--
--   * entry_key is the FINAL keyset tiebreaker. It was already carried in the
--     cursor but never used for comparison; adding it makes the order TOTAL,
--     closing the pre-existing gap where target-split siblings shared
--     (posted_at, header_seq) and could straddle a page boundary.
--
-- LANGUAGE is plpgsql (was sql) so the ORDER BY / keyset operator can be built
-- from the whitelisted column + direction via format(). STABLE + PARALLEL SAFE
-- are retained (the executed statement is a pure SELECT over the view).

DROP FUNCTION IF EXISTS register_entry_keys(UUID, UUID, TIMESTAMPTZ, BIGINT, TEXT, INTEGER, BOOLEAN,
    TEXT, DATE, DATE, NUMERIC, NUMERIC, UUID, TEXT, UUID, TEXT, DATE);

CREATE FUNCTION register_entry_keys(
    p_account_id        UUID,
    p_ledger_id         UUID,
    p_cursor_entry_key  UUID,               -- was p_cursor_posted_at (mig 165)
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
    -- a single scalar. Same visibility/scope predicate as the main query.
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
            FROM resolved_transactions rt
            WHERE rt.is_hidden = $3
              AND rt.is_merged_into IS NULL
              AND ($1 IS NULL OR rt.account_id = $1)
              AND ($1 IS NOT NULL OR EXISTS (
                    SELECT 1 FROM accounts a WHERE a.id = rt.account_id AND a.ledger_id = $2))
              -- Filters (mig 164). Each is a no-op when its arg is NULL.
              AND ($4  IS NULL OR rt.posted_at::date >= $4)
              AND ($5  IS NULL OR rt.posted_at::date <= $5)
              AND ($6  IS NULL OR ABS(COALESCE(rt.header_account_net_amount, rt.amount)) >= $6)
              AND ($7  IS NULL OR ABS(COALESCE(rt.header_account_net_amount, rt.amount)) <= $7)
              AND ($8  IS NULL OR rt.security_id = $8)
              AND ($9  IS NULL OR rt.counterparty_account_id = $9)
              AND ($10 IS NULL OR $10 = ANY(rt.tags))
              AND ($11 IS NULL OR (
                    rt.payee ILIKE '%%' || $11 || '%%'
                 OR rt.memo ILIKE '%%' || $11 || '%%'
                 OR rt.check_number ILIKE '%%' || $11 || '%%'
                 OR rt.counterparty_account_name ILIKE '%%' || $11 || '%%'
                 OR EXISTS (SELECT 1 FROM unnest(rt.tags) tg WHERE tg ILIKE '%%' || $11 || '%%')))
              AND (
                    $12 IS NULL
                 OR ($12 = 'needs_review' AND rt.needs_review = TRUE)
                 OR ($12 = 'scheduled'    AND rt.posted_at::date > COALESCE($13, CURRENT_DATE))
                 OR ($12 = 'cleared'     AND rt.status = 'cleared'     AND rt.is_pending = FALSE AND rt.posted_at::date <= COALESCE($13, CURRENT_DATE))
                 OR ($12 = 'uncleared'   AND rt.status = 'uncleared'   AND rt.is_pending = FALSE AND rt.posted_at::date <= COALESCE($13, CURRENT_DATE))
                 OR ($12 = 'reconciling' AND rt.status = 'reconciling' AND rt.is_pending = FALSE AND rt.posted_at::date <= COALESCE($13, CURRENT_DATE)))
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
