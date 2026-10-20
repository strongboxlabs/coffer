-- Phase 1: resolved_transactions view (§4.3 of the architecture doc).
-- Application code reads from this view exclusively; only the importer and sync
-- service touch the raw transactions table.

CREATE VIEW resolved_transactions AS
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
    t.created_at
FROM transactions t
LEFT JOIN transaction_overrides o ON o.transaction_id = t.id;
