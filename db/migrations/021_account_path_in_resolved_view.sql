-- 021_account_path_in_resolved_view.sql
--
-- The register's category chip needs the full root-to-leaf path
-- ("Wages & Salary/Base", "Taxes/Federal Income Tax"), not just the
-- leaf name. Leaves alone often have ambiguous one-word labels
-- ("Base", "Health", "Salary") that mean nothing out of context.
--
-- account_path
-- ------------
-- Stored function (not a recursive subquery inlined per row) so the
-- view stays readable and other callers can reuse it later. STABLE +
-- PARALLEL SAFE; runs under the caller's role with no SECURITY
-- DEFINER, so RLS on accounts evaluates as usual through the
-- security_invoker resolved_transactions view.
--
-- Depth in real data is 1-3 levels (spot-check: 437 root / 185 mid /
-- 7 leaf accounts), bounded by Moneydance's category UI conventions.
-- The recursive walk is at most three PK lookups per call.
--
-- View rewrite
-- ------------
-- resolved_transactions.counterparty_account_name now carries the
-- full path instead of just the leaf. The column is kept under the
-- same name (semantic is "human-readable counterparty"; only the
-- format changes), so the API DTO and UI chip pick up the change
-- with no code modifications. CREATE OR REPLACE VIEW preserves
-- shape; we re-assert security_invoker because CREATE OR REPLACE
-- drops reloptions.
--
-- Separator
-- ---------
-- Slash ('/') per product decision. Reads compactly, doesn't
-- collide with category names that happen to contain ':' or '>',
-- and stays unambiguous in long paths.

CREATE FUNCTION account_path(p_account_id UUID)
RETURNS TEXT
LANGUAGE SQL
STABLE
PARALLEL SAFE
AS $$
    WITH RECURSIVE chain AS (
        SELECT id, name, parent_id, 1 AS depth
        FROM accounts
        WHERE id = p_account_id
        UNION ALL
        SELECT a.id, a.name, a.parent_id, c.depth + 1
        FROM accounts a
        JOIN chain c ON a.id = c.parent_id
    )
    SELECT string_agg(name, '/' ORDER BY depth DESC)
    FROM chain;
$$;

GRANT EXECUTE ON FUNCTION account_path(UUID) TO coffer_app;

CREATE OR REPLACE VIEW resolved_transactions AS
SELECT
    t.id,
    t.account_id,
    COALESCE(o.payee,         t.feed_payee)         AS payee,
    COALESCE(o.memo,          t.feed_memo)          AS memo,
    COALESCE(o.amount,        t.feed_amount)        AS amount,
    COALESCE(o.posted_at,     t.feed_posted_at)     AS posted_at,
    COALESCE(o.transacted_at, t.feed_transacted_at) AS transacted_at,
    COALESCE(o.status,        t.feed_status)        AS status,
    COALESCE(o.is_hidden,     FALSE)                AS is_hidden,
    (o.id IS NOT NULL)                              AS has_overrides,
    t.balance_after,
    t.origin,
    t.is_pending,
    t.is_merged_into,
    t.investment_action,
    t.external_id,
    t.created_at,
    t.check_number,
    t.counterparty_id,
    t.txn_group_id,
    t.leg_index,
    ct.account_id              AS counterparty_account_id,
    account_path(ct.account_id) AS counterparty_account_name,
    ca.account_type            AS counterparty_account_type,
    COALESCE(
        ARRAY(SELECT tg.name
              FROM transaction_tags tt
              JOIN tags tg ON tg.id = tt.tag_id
              WHERE tt.transaction_id = t.id
              ORDER BY tg.name),
        ARRAY[]::TEXT[]
    ) AS tags
FROM transactions t
LEFT JOIN transaction_overrides o ON o.transaction_id = t.id
LEFT JOIN transactions ct ON ct.id = t.counterparty_id
LEFT JOIN accounts ca ON ca.id = ct.account_id;

ALTER VIEW resolved_transactions SET (security_invoker = true);
