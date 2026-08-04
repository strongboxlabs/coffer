-- 031_register_entry_keys_bidirectional.sql
-- =================================================================
--
-- Add bidirectional pagination to `register_entry_keys`.
--
-- BEFORE
-- ------
-- The function paginates one direction (time-DESC, strictly older
-- than the supplied cursor). `LOAD MORE` button extends downward;
-- there's no way to fetch entries newer than the cursor.
--
-- AFTER
-- -----
-- A new `p_direction TEXT` parameter accepts `'before'` (older,
-- existing behaviour) or `'after'` (newer). The HAVING clause and
-- ORDER BY flip accordingly so the LIMIT picks the K entries
-- ADJACENT to the cursor — newest-of-older for 'before', oldest-
-- of-newer for 'after'. An outer SELECT re-sorts the result set
-- to time-DESC regardless of direction so consumers get a uniform
-- shape.
--
-- WHY
-- ---
-- Unblocks sliding-window pagination on the SPA: a windowed
-- cursor pair (top + bottom) can fetch in either direction as the
-- user approaches scroll edges. Also unblocks the Show-Other-Side
-- arrival path (PR #50 stopgap): the SPA can call this function
-- with `direction='before'` anchored at the focused entry's
-- cursor, then immediately call it again with `direction='after'`
-- to prepend newer history into the window.
--
-- The `starting_at=<headerId>` lookup (header id → cursor tuple)
-- lives in the C# repo (`RegisterRepository.ResolveCursorForHeader`)
-- rather than as a separate SQL function: it's a single one-line
-- query that the EF LINQ surface expresses cleanly, and keeping
-- it on the .NET side keeps the SQL function count tight.
--
-- DROP-then-CREATE: the function signature gains a parameter, and
-- `CREATE OR REPLACE FUNCTION` only works when the parameter list
-- is unchanged. The new 7-argument signature replaces the old
-- 6-arg binding in EF's HasDbFunction mapping in the same PR.
-- =================================================================

DROP FUNCTION register_entry_keys(UUID, UUID, TIMESTAMPTZ, TIMESTAMPTZ, UUID, INTEGER);

CREATE FUNCTION register_entry_keys(
    p_account_id        UUID,
    p_ledger_id         UUID,
    p_cursor_posted_at  TIMESTAMPTZ,
    p_cursor_created_at TIMESTAMPTZ,
    p_cursor_entry_key  UUID,
    p_direction         TEXT,
    p_limit             INTEGER
)
RETURNS TABLE(
    posted_at  TIMESTAMPTZ,
    created_at TIMESTAMPTZ,
    entry_key  UUID
)
LANGUAGE SQL
STABLE
PARALLEL SAFE
AS $$
    -- Inner query: picks K entries closest to the cursor in the
    -- chosen direction. For 'before', the LIMIT picks the
    -- newest-of-older set (ORDER BY DESC). For 'after', the LIMIT
    -- picks the oldest-of-newer set (ORDER BY ASC). Either way,
    -- the K rows are guaranteed to be contiguous with the cursor
    -- on the keyset timeline.
    --
    -- The CASE-guarded ORDER BY columns are how we flip direction
    -- without two parallel function bodies: when p_direction is
    -- 'after', the ASC columns are non-NULL and drive ordering;
    -- the DESC fallback rows are tiebroken by them. When 'before',
    -- the ASC columns evaluate to NULL for every row (no-op in
    -- ORDER BY) and the DESC fallback drives ordering.
    SELECT posted_at, created_at, entry_key FROM (
        SELECT
            MAX(rt.posted_at)                                  AS posted_at,
            MAX(rt.created_at)                                 AS created_at,
            COALESCE(rt.txn_group_id, rt.id)                   AS entry_key
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
        GROUP BY COALESCE(rt.txn_group_id, rt.id)
        HAVING
            -- No cursor → return the first K of whichever direction's
            -- ORDER BY is. 'before' with no cursor is the canonical
            -- initial page (newest K entries). 'after' with no cursor
            -- is unusual but defensibly returns oldest K — the caller
            -- is expected to anchor 'after' with a real cursor.
            p_cursor_posted_at IS NULL
            OR (p_direction = 'before' AND (
                MAX(rt.posted_at) < p_cursor_posted_at
                OR (MAX(rt.posted_at) = p_cursor_posted_at
                    AND MAX(rt.created_at) < p_cursor_created_at)
                OR (MAX(rt.posted_at) = p_cursor_posted_at
                    AND MAX(rt.created_at) = p_cursor_created_at
                    AND COALESCE(rt.txn_group_id, rt.id) < p_cursor_entry_key)
            ))
            OR (p_direction = 'after' AND (
                MAX(rt.posted_at) > p_cursor_posted_at
                OR (MAX(rt.posted_at) = p_cursor_posted_at
                    AND MAX(rt.created_at) > p_cursor_created_at)
                OR (MAX(rt.posted_at) = p_cursor_posted_at
                    AND MAX(rt.created_at) = p_cursor_created_at
                    AND COALESCE(rt.txn_group_id, rt.id) > p_cursor_entry_key)
            ))
        ORDER BY
            CASE WHEN p_direction = 'after' THEN MAX(rt.posted_at) END ASC,
            CASE WHEN p_direction = 'after' THEN MAX(rt.created_at) END ASC,
            CASE WHEN p_direction = 'after' THEN COALESCE(rt.txn_group_id, rt.id) END ASC,
            MAX(rt.posted_at) DESC,
            MAX(rt.created_at) DESC,
            COALESCE(rt.txn_group_id, rt.id) DESC
        LIMIT p_limit
    ) sub
    -- Outer SELECT re-sorts to uniform time-DESC so consumers get
    -- the same shape regardless of direction.
    ORDER BY posted_at DESC, created_at DESC, entry_key DESC;
$$;

GRANT EXECUTE ON FUNCTION
    register_entry_keys(UUID, UUID, TIMESTAMPTZ, TIMESTAMPTZ, UUID, TEXT, INTEGER)
    TO coffer_app;
