-- 019_register_entry_functions.sql
--
-- Postgres function that drives the entry-keyed page of the register
-- endpoint (the "100 complete sets" model agreed after the PR 5.2
-- spot-check). The function lives here rather than inline in the
-- C# repository because:
--
--   * SQL belongs with the schema it queries — version-controlled
--     in the migrations folder, syntax-checked on every CI run via
--     db/test/, and inspectable from psql.
--   * The API surface becomes a clean RPC: one function call per
--     query, no ad-hoc string assembly in the data-access layer.
--   * Refactoring a query (e.g. adding a column to the resolved
--     view, changing the cursor shape) updates this file alongside
--     the schema change, not three layers of C# wrappers.
--
-- The function is STABLE PARALLEL SAFE — read-only with output
-- determined by args + the current snapshot, so Postgres can
-- parallelize and inline it aggressively.
--
-- Q2 ("fetch every row belonging to those entries") is intentionally
-- NOT a function. We tried it, and the SETOF wrapper produced an
-- opaque function scan that the planner couldn't push filters into
-- (cost was the default `rows=1000` opaque-SETOF estimate, plan time
-- 312ms vs 7ms for the equivalent direct view query). The OR-on-null
-- account-scope pattern compounded it — the planner couldn't fold
-- away the unused branch. Q2 now runs as plain LINQ over the view
-- (no raw SQL in C#) which lets EF emit literal account predicates.
--
-- RLS: the function invokes the security_invoker view
-- (resolved_transactions) which evaluates policies as the caller's
-- role. No SECURITY DEFINER — intentionally doesn't elevate. The
-- coffer_app role gets EXECUTE explicitly so the API can call it.

-- ---------------------------------------------------------------------------
-- register_entry_keys
--
-- Returns up to `p_limit` register-entry identifiers, ordered by
-- (posted_at DESC, entry_key DESC), starting at the cursor. An
-- entry's key is COALESCE(txn_group_id, id) — multi-split groups
-- collapse to one row; single transactions are their own entry.
--
-- Cursor semantics: pass NULLs for p_cursor_posted_at and
-- p_cursor_entry_key to start at the most recent entry. To fetch
-- the next page, pass the (posted_at, entry_key) of the LAST entry
-- on the previous page; this function returns entries strictly
-- after that point in DESC order.
--
-- p_account_id NULL → ledger-wide view (subject to RLS).
-- p_account_id NOT NULL → narrow to one account; the caller is
-- responsible for verifying that account belongs to the ledger
-- (the endpoint does this via accounts.BelongsToLedgerAsync).
-- ---------------------------------------------------------------------------
CREATE FUNCTION register_entry_keys(
    p_account_id        UUID,
    p_ledger_id         UUID,
    p_cursor_posted_at  TIMESTAMPTZ,
    p_cursor_entry_key  UUID,
    p_limit             INTEGER
)
RETURNS TABLE(
    posted_at TIMESTAMPTZ,
    entry_key UUID
)
LANGUAGE SQL
STABLE
PARALLEL SAFE
AS $$
    SELECT
        MAX(rt.posted_at)                                  AS posted_at,
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
            AND COALESCE(rt.txn_group_id, rt.id) < p_cursor_entry_key
        )
    ORDER BY MAX(rt.posted_at) DESC, COALESCE(rt.txn_group_id, rt.id) DESC
    LIMIT p_limit;
$$;

-- Grant EXECUTE to the runtime app role so the API can call the
-- function through coffer_app. The function itself doesn't elevate
-- privileges; it reads through the security_invoker view.
GRANT EXECUTE ON FUNCTION register_entry_keys(UUID, UUID, TIMESTAMPTZ, UUID, INTEGER) TO coffer_app;
