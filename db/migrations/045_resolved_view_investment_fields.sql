-- 045_resolved_view_investment_fields.sql
--
-- Slice A1.c — investment register row rendering.
--
-- The register today renders investment txns (Buy / Sell / Div /
-- DivReinvest / Interest / Transfer / MiscInc / MiscExp / Split)
-- as generic Payee/Memo rows because the investment-flavoured
-- fields on `txn_legs` (security_id, quantity, unit_price) aren't
-- projected through `resolved_transactions`. This migration
-- extends the view to expose them, plus joins to `securities` to
-- bring the ticker + name through in one query.
--
-- `investment_action` is already on the view (since migration 005);
-- this slice adds the per-row data the SPA needs to render an
-- Action chip + ticker badge + qty×price subtitle.
--
-- NOT included: `txn_legs.commission`. Audit confirmed the column
-- is dead — 0 of 130K rows have a non-zero value; the importer
-- writes 0/null on every row. ADR-0019 Rule 5 makes the fee leg
-- (separate paired txn_headers row) the source of truth; the lot's
-- `unit_cost` carries the apportioned commission for cost-basis
-- math. Dropping `txn_legs.commission` itself is tracked under
-- follow-ups.md.
--
-- CREATE OR REPLACE VIEW requires the column list to be a strict
-- extension — same columns in the same order, then additions at the
-- end. The body below mirrors migration 037 (latest definition).

CREATE OR REPLACE VIEW resolved_transactions AS
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
    -- ----- Slice A1.c additions -----
    -- Per-leg investment metadata is on the holdings-side leg of a
    -- Buy/Sell/Div/etc.; the cash-side leg has it as NULL. The SPA
    -- needs these on whichever leg it's rendering: a brokerage
    -- account view shows the CASH side (with NULL investment fields)
    -- but the matching counterparty leg on the Holdings sibling
    -- carries the security + quantity + price. For the register row
    -- to show "Buy IDXA 100 × $10.00" without a second round-trip,
    -- we read the investment metadata off the LEG row this view
    -- represents AND off the counterparty leg, preferring the
    -- non-null one (the holdings-side carries the data; the cash-
    -- side carries NULL). COALESCE pulls whichever side has it.
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
-- securities join is on whichever side carries the security_id;
-- LEFT so non-investment legs (the bulk of the view) skip the
-- join cost and project NULLs for ticker/name.
LEFT JOIN securities s
    ON s.id = COALESCE(l.security_id, other.security_id);

ALTER VIEW resolved_transactions SET (security_invoker = true);
