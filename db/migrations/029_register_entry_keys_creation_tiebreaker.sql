-- 029_register_entry_keys_creation_tiebreaker.sql
-- =================================================================
--
-- Add `created_at` as a sort tiebreaker to `register_entry_keys`
-- (originally migration 019).
--
-- Why: under the original sort `(posted_at DESC, entry_key DESC)`,
-- two transactions on the same posted_at date sorted by their
-- entry_key UUID — essentially random ordering. The visible
-- symptom: a freshly-created manual transaction often appeared
-- BELOW older same-day transactions in the register, which is the
-- opposite of what users expect ("the one I just added should be
-- at the top").
--
-- Fix: add `created_at` (MAX across the entry's legs) as a
-- secondary sort key, so newer creations win the same-posted-at
-- tiebreak. Final order: `(posted_at DESC, created_at DESC,
-- entry_key DESC)`. Cursor codec on the API side carries all
-- three components so keyset pagination remains stable across
-- pages.
--
-- DROP-then-CREATE: the function signature gains a parameter
-- (`p_cursor_created_at`), and `CREATE OR REPLACE FUNCTION` only
-- works when the parameter list is unchanged. The new signature
-- replaces the old binding in EF's HasDbFunction mapping in the
-- same PR.
-- =================================================================

DROP FUNCTION register_entry_keys(UUID, UUID, TIMESTAMPTZ, UUID, INTEGER);

CREATE FUNCTION register_entry_keys(
    p_account_id        UUID,
    p_ledger_id         UUID,
    p_cursor_posted_at  TIMESTAMPTZ,
    p_cursor_created_at TIMESTAMPTZ,
    p_cursor_entry_key  UUID,
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
        p_cursor_posted_at IS NULL
        OR MAX(rt.posted_at) < p_cursor_posted_at
        OR (
            MAX(rt.posted_at) = p_cursor_posted_at
            AND MAX(rt.created_at) < p_cursor_created_at
        )
        OR (
            MAX(rt.posted_at) = p_cursor_posted_at
            AND MAX(rt.created_at) = p_cursor_created_at
            AND COALESCE(rt.txn_group_id, rt.id) < p_cursor_entry_key
        )
    ORDER BY
        MAX(rt.posted_at) DESC,
        MAX(rt.created_at) DESC,
        COALESCE(rt.txn_group_id, rt.id) DESC
    LIMIT p_limit;
$$;

GRANT EXECUTE ON FUNCTION
    register_entry_keys(UUID, UUID, TIMESTAMPTZ, TIMESTAMPTZ, UUID, INTEGER)
    TO coffer_app;
