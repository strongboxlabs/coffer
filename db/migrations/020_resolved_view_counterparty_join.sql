-- 020_resolved_view_counterparty_join.sql
--
-- Rewrites resolved_transactions to use LEFT JOINs against the
-- counterparty row + accounts table instead of three correlated
-- scalar subqueries (counterparty_account_id, _name, _type).
--
-- Why
-- ---
-- EXPLAIN ANALYZE on a 100-entry register page showed
-- register_entry_rows running 289ms with ~25K disk reads, dominated
-- by ~1000 scalar subquery evaluations against the cold transactions
-- + accounts pages (3 subqueries × ~321 rows per page). A single
-- hash/merge join plans these as ONE per-row lookup instead of three,
-- cutting the page query time substantially.
--
-- Output shape
-- ------------
-- Every column name, type, and order is preserved so the EF model
-- (ResolvedTransactionView) and HasDbFunction bindings keep working
-- without modification. CREATE OR REPLACE VIEW is valid here because
-- we're only changing the SELECT list's *expressions*, not its
-- column names or types.
--
-- LEFT vs INNER
-- -------------
-- counterparty_id is NOT NULL with an FK, so an INNER JOIN would
-- always match — except under RLS. When a counterparty row sits in
-- a ledger the caller can't see (shouldn't happen given per-ledger
-- scoping, but defensive against future cross-ledger features),
-- INNER JOIN would silently drop the outer row. LEFT JOIN surfaces
-- the row with NULL counterparty_* columns, which is the correct
-- behaviour for the user's own data.
--
-- security_invoker
-- ----------------
-- CREATE OR REPLACE VIEW drops the view's reloptions (see migration
-- 018's comment). We re-assert security_invoker = true so the RLS
-- policies on transactions / accounts / tags evaluate against the
-- caller's role, not the view owner's — same guard as before.

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
    -- Counterparty resolution via JOIN, replacing migration 018's
    -- three scalar subqueries.
    ct.account_id           AS counterparty_account_id,
    ca.name                 AS counterparty_account_name,
    ca.account_type         AS counterparty_account_type,
    -- Tags stays as a per-row ARRAY-agg. Per-row cost is small
    -- (transaction_tags PK is (transaction_id, tag_id); typical
    -- rows have 0-1 tags) and rewriting it as a LATERAL JOIN +
    -- array_agg adds complexity without obvious win. Revisit if a
    -- future EXPLAIN flags it.
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
