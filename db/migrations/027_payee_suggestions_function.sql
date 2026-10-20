-- 027_payee_suggestions_function.sql
-- =================================================================
--
-- ledger_payee_suggestions
-- ------------------------
-- Aggregation behind the SPA's payee-typeahead endpoint. Returns the
-- distinct resolved payees in a ledger, ranked by usage count then
-- recency. Hidden + merged-away headers are excluded. The override
-- layer's payee value (when present) wins over the header's own —
-- same precedence resolved_transactions uses, applied here at the
-- header level so a multi-leg posting is counted once.
--
-- Lives in SQL rather than EF Core LINQ because the COALESCE-on-an-
-- outer-join pattern doesn't translate (tried; EF rejects the
-- conditional on the right side of a LEFT JOIN). Per ADR-0005 + the
-- engineering-standards memo, complex query shapes live in Postgres
-- and the API binds them via HasDbFunction.
--
-- Parameters
-- ----------
--   p_ledger_id : the ledger to aggregate over. RLS is unnecessary
--                 here because the API verifies ledger visibility
--                 before calling; the function is SECURITY INVOKER
--                 so ledger_app would still see only its own rows
--                 from txn_headers / txn_header_overrides under
--                 migration 022's policies.
--   p_limit    : cap on the returned set. The API passes 500; the
--                 function clamps to [1, 10000] for safety.
--
-- Returns one row per distinct resolved payee:
--   name           TEXT         -- the resolved payee value
--   count          BIGINT       -- number of headers resolving to it
--   last_used_at   TIMESTAMPTZ  -- most recent posted_at among those
-- =================================================================

CREATE OR REPLACE FUNCTION ledger_payee_suggestions(
    p_ledger_id UUID,
    p_limit     INT)
RETURNS TABLE (
    name         TEXT,
    count        BIGINT,
    last_used_at TIMESTAMPTZ)
LANGUAGE SQL
STABLE
PARALLEL SAFE
AS $$
    SELECT
        resolved.payee                AS name,
        COUNT(*)                      AS count,
        MAX(resolved.posted_at)       AS last_used_at
    FROM (
        SELECT
            COALESCE(o.payee,     h.payee)     AS payee,
            COALESCE(o.is_hidden, h.is_hidden) AS is_hidden,
            h.posted_at
        FROM txn_headers h
        LEFT JOIN txn_header_overrides o ON o.header_id = h.id
        WHERE h.ledger_id        = p_ledger_id
          AND h.is_merged_into   IS NULL
    ) resolved
    WHERE resolved.payee     IS NOT NULL
      AND NOT resolved.is_hidden
    GROUP BY resolved.payee
    ORDER BY count DESC, last_used_at DESC
    LIMIT GREATEST(1, LEAST(p_limit, 10000));
$$;

GRANT EXECUTE ON FUNCTION ledger_payee_suggestions(UUID, INT) TO coffer_app, coffer_service;
