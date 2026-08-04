-- 047_move_action_to_header.sql
--
-- Slice A1.d (investment register layout rework): the transaction's
-- *action* (Buy / Sell / Div / etc.) is a property of the whole event,
-- not of an individual leg. All legs of one investment transaction
-- share the same action by definition; storing it per-leg duplicated
-- the same string across every leg of every investment txn.
--
-- This migration moves `investment_action` from `txn_legs` to
-- `txn_headers.action`. After this slice, the editor and register
-- read `header.action`; the leg column is gone.
--
-- Audit before the move (real dev DB, ~130K legs):
--   * Most investment headers have uniform action across all legs.
--   * 1,033 MD-imported headers DO have conflicting leg actions —
--     specifically the buyx / sellx / divx shapes where the
--     principal posting carries the buy/sell/dividend_cash action
--     and a second posting carries 'transfer' (the cash source/sink
--     external account). Patterns:
--         {buy,transfer}           777 headers
--         {sell,transfer}          157
--         {dividend_cash,transfer}  99
--   * In all conflict cases the principal action is the meaningful
--     one — the second posting's 'transfer' was redundant
--     bookkeeping about leg ROLE, not a separate action concept.
--     Backfill rule: when conflict, pick the non-transfer action.
--
-- ORDER MATTERS:
--   1) ADD COLUMN txn_headers.action (nullable, CHECK on the 9-value set)
--   2) BACKFILL from any leg's investment_action (DISTINCT non-null)
--   3) Rebuild resolved_transactions view to project header.action
--      (the view currently selects leg.investment_action; can't drop
--       the leg column while a view references it)
--   4) ALTER TABLE txn_legs DROP COLUMN investment_action
--
-- Existing CHECK constraint values come from migration 043's
-- final 9-action set: buy, sell, dividend_cash, dividend_reinvest,
-- interest, transfer, misc_income, misc_expense, split.

BEGIN;

-- 1. Add the column. Nullable because non-investment headers (bank
-- payments, manual splits, etc.) have no action. Investment editor
-- enforces non-null at write time.
ALTER TABLE txn_headers
    ADD COLUMN action TEXT
    CHECK (action IS NULL OR action IN (
        'buy', 'sell',
        'dividend_cash', 'dividend_reinvest',
        'interest',
        'transfer',
        'misc_income', 'misc_expense',
        'split'
    ));

-- 2. Backfill: lift the PRIMARY action to the header. For headers
-- with conflicting leg actions (MD's buyx / sellx / divx — see audit
-- comment above), prefer the non-transfer action since 'transfer'
-- on a second posting is structural noise, not a distinct event
-- type. For genuine cross-account cash transfers (all legs marked
-- 'transfer'), the header.action becomes 'transfer' naturally.
UPDATE txn_headers h
   SET action = sub.primary_action
  FROM (
      SELECT header_id,
             COALESCE(
                 MAX(investment_action) FILTER (WHERE investment_action <> 'transfer'),
                 MAX(investment_action)
             ) AS primary_action
        FROM txn_legs
       WHERE investment_action IS NOT NULL
       GROUP BY header_id
  ) sub
 WHERE h.id = sub.header_id;

-- 3. Rebuild resolved_transactions to read header.action instead
-- of leg.investment_action. Same column-list extension shape as
-- migration 045: all prior columns first, additions at the end.
-- Re-issuing the FULL body (per the migration-037 convention) so
-- the view definition is self-contained at this point in history.
DROP VIEW resolved_transactions;
CREATE VIEW resolved_transactions AS
SELECT
    l.id,
    l.account_id,
    COALESCE(o.payee,            h.payee)                              AS payee,
    COALESCE(lo.leg_memo, l.leg_memo, o.memo, h.memo)                  AS memo,
    COALESCE(lo.amount,          l.amount)                             AS amount,
    COALESCE(o.posted_at,        h.posted_at)                          AS posted_at,
    COALESCE(o.transacted_at,    h.transacted_at)                      AS transacted_at,
    h.status                                                           AS status,
    COALESCE(o.is_hidden,        h.is_hidden, FALSE)                   AS is_hidden,
    (o.header_id IS NOT NULL OR lo.leg_id IS NOT NULL)                 AS has_overrides,
    l.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    h.action                                                           AS investment_action,
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
    h.id                                                               AS header_id,
    h.cleared_at                                                       AS cleared_at,
    h.cleared_by_user_id                                               AS cleared_by_user_id,
    COALESCE(lo.leg_memo, l.leg_memo)                                  AS leg_memo,
    COALESCE(o.memo, h.memo)                                           AS header_memo,
    h.online_match_fitid                                               AS online_match_fitid,
    h.online_match_fi_id                                               AS online_match_fi_id,
    h.online_match_status                                              AS online_match_status,
    h.online_match_type                                                AS online_match_type,
    h.online_match_orig_id                                             AS online_match_orig_id,
    h.needs_review                                                     AS needs_review,
    COALESCE(l.security_id, other.security_id)                          AS security_id,
    s.ticker                                                            AS security_ticker,
    s.name                                                              AS security_name,
    COALESCE(l.quantity, other.quantity)                                AS quantity,
    COALESCE(l.unit_price, other.unit_price)                            AS unit_price
FROM txn_legs l
JOIN txn_headers h ON h.id = l.header_id
LEFT JOIN txn_header_overrides o ON o.header_id = h.id
LEFT JOIN txn_leg_overrides    lo ON lo.leg_id  = l.id
LEFT JOIN txn_legs other
    ON other.header_id = l.header_id
    AND other.posting_index = l.posting_index
    AND other.id != l.id
LEFT JOIN accounts ca ON ca.id = other.account_id
LEFT JOIN securities s
    ON s.id = COALESCE(l.security_id, other.security_id);

ALTER VIEW resolved_transactions SET (security_invoker = true);
GRANT SELECT ON resolved_transactions TO coffer_app, coffer_service;

-- 4. Drop the leg column. View no longer references it.
ALTER TABLE txn_legs DROP COLUMN investment_action;

COMMIT;
