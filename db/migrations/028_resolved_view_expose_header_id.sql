-- 028_resolved_view_expose_header_id.sql
-- =================================================================
--
-- Expose header_id on resolved_transactions
-- ------------------------------------------
-- The SPA's inline-edit flow needs to POST against
-- /api/.../transactions/{headerId}/header-overrides, but the
-- resolved view currently projects only the leg id and the
-- group-or-NULL `txn_group_id`. Singles get NULL `txn_group_id` by
-- design (the API's AssembleEntries treats them as their own
-- entry), so there's no way for the SPA to recover the header from
-- the row.
--
-- Fix: add `header_id` at the end of the projection so every row
-- carries the header identity unambiguously. Backwards-compatible:
-- the existing columns keep their names + types + order; new
-- consumers opt in.
--
-- CREATE OR REPLACE constraints: Postgres allows replacing a view
-- only when the existing columns match name/type/order; new
-- columns may be appended. The full SELECT is repeated below
-- (migrations 023 + 028 are the canonical pair).
-- =================================================================

CREATE OR REPLACE VIEW resolved_transactions AS
SELECT
    l.id,
    l.account_id,
    COALESCE(o.payee,            h.payee)                              AS payee,
    COALESCE(lo.leg_memo, l.leg_memo, o.memo, h.memo)                  AS memo,
    COALESCE(lo.amount,          l.amount)                             AS amount,
    COALESCE(o.posted_at,        h.posted_at)                          AS posted_at,
    COALESCE(o.transacted_at,    h.transacted_at)                      AS transacted_at,
    COALESCE(o.status,           h.status)                             AS status,
    COALESCE(o.is_hidden,        h.is_hidden, FALSE)                   AS is_hidden,
    (o.header_id IS NOT NULL OR lo.leg_id IS NOT NULL)                 AS has_overrides,
    l.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    l.investment_action,
    h.external_id,
    l.created_at,
    COALESCE(o.check_number,     h.check_number)                       AS check_number,
    other.id                                                           AS counterparty_id,
    CASE WHEN EXISTS (
        SELECT 1 FROM txn_legs g
        WHERE g.header_id = h.id AND g.posting_index > 0
    ) THEN h.id ELSE NULL END                                          AS txn_group_id,
    l.posting_index                                                    AS leg_index,
    other.account_id                                                   AS counterparty_account_id,
    account_path(other.account_id)                                     AS counterparty_account_name,
    ca.account_type                                                    AS counterparty_account_type,
    COALESCE(
        ARRAY(SELECT tg.name
              FROM txn_header_tags tt
              JOIN tags tg ON tg.id = tt.tag_id
              WHERE tt.header_id = h.id
              ORDER BY tg.name),
        ARRAY[]::TEXT[]
    )                                                                  AS tags,
    -- New in migration 028: unconditional header identity. The SPA's
    -- inline-edit flow uses this directly; downstream consumers that
    -- still prefer the legacy txn_group_id semantics ignore it.
    h.id                                                               AS header_id
FROM txn_legs l
JOIN txn_headers h ON h.id = l.header_id
LEFT JOIN txn_header_overrides o ON o.header_id = h.id
LEFT JOIN txn_leg_overrides    lo ON lo.leg_id  = l.id
LEFT JOIN txn_legs other
    ON other.header_id = l.header_id
    AND other.posting_index = l.posting_index
    AND other.id != l.id
LEFT JOIN accounts ca ON ca.id = other.account_id;

-- Re-assert security_invoker on the view. CREATE OR REPLACE VIEW
-- preserves rule rewrites but reloptions occasionally drift across
-- Postgres versions; the explicit ALTER is cheap idempotent
-- insurance that RLS continues to apply to the caller (migration
-- 017's posture).
ALTER VIEW resolved_transactions SET (security_invoker = true);
