-- 018_resolved_view_register_parity.sql
--
-- Extends `resolved_transactions` with the columns the register UI
-- needs to reach feature parity with Moneydance:
--
--   * counterparty_account_id / _name / _type — the "other side" of
--     the symmetric posting (ADR-0019). The MD register shows this
--     as the Category column (e.g. "Bills:Electricity"); we surface
--     the raw account fields so the SPA can render the chip + drill
--     into the account.
--   * tags — text[] aggregated from transaction_tags. Empty array
--     when none, never NULL, so the SPA can iterate without a null
--     guard.
--   * check_number — plain projection of t.check_number. The MD
--     register surfaces this in its Check# column.
--   * counterparty_id / txn_group_id / leg_index — projected so the
--     SPA can group multi-split events into one row (follow-up; see
--     docs/follow-ups.md §7). counterparty_id was always
--     reachable via a join but exposing it directly avoids that
--     round-trip.
--
-- The view still uses security_invoker = true (set by migration 017),
-- so every new subquery here respects RLS — counterparty rows the
-- caller can't see are filtered out (returning NULLs for the
-- counterparty_* columns); tags the caller can't see drop out of
-- the aggregation.
--
-- CREATE OR REPLACE VIEW is allowed to add columns to the end of
-- the column list; existing columns keep their names, order, and
-- types so any caller that selected by name (the EF query type)
-- keeps working.

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
    -- New columns appended below (CREATE OR REPLACE only allows
    -- additive extension at the tail).
    t.check_number,
    t.counterparty_id,
    t.txn_group_id,
    t.leg_index,
    -- Counterparty account exposure for the Category column. The
    -- subqueries hit transactions by PK and accounts by PK — both
    -- indexed lookups. NULL when counterparty_id is unreachable
    -- under RLS (e.g. a cross-ledger leg the caller can't see; not
    -- expected in practice).
    (SELECT ct.account_id
     FROM transactions ct
     WHERE ct.id = t.counterparty_id) AS counterparty_account_id,
    (SELECT ca.name
     FROM transactions ct
     JOIN accounts ca ON ca.id = ct.account_id
     WHERE ct.id = t.counterparty_id) AS counterparty_account_name,
    (SELECT ca.account_type
     FROM transactions ct
     JOIN accounts ca ON ca.id = ct.account_id
     WHERE ct.id = t.counterparty_id) AS counterparty_account_type,
    -- Tags as a deterministically-ordered text[]. COALESCE wraps so
    -- the column is never NULL (callers iterate without a guard).
    COALESCE(
        ARRAY(SELECT tg.name
              FROM transaction_tags tt
              JOIN tags tg ON tg.id = tt.tag_id
              WHERE tt.transaction_id = t.id
              ORDER BY tg.name),
        ARRAY[]::TEXT[]
    ) AS tags
FROM transactions t
LEFT JOIN transaction_overrides o ON o.transaction_id = t.id;

-- CREATE OR REPLACE VIEW drops the view's reloptions, so we must
-- re-assert `security_invoker = true` (originally set by migration
-- 017) for the underlying RLS policies on transactions / accounts /
-- tags to be evaluated against the *caller's* role rather than the
-- view owner's. Without this line, alice's query would see bob's
-- data through this view — an RLS regression caught by
-- RowLevelSecurityTests.Transactions_inherit_account_policy_via_FK_chain.
ALTER VIEW resolved_transactions SET (security_invoker = true);
