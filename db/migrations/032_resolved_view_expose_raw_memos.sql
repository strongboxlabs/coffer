-- Surface raw leg_memo + header_memo on resolved_transactions
-- (ADR-0025 follow-up). The existing `memo` column is a 4-way
-- COALESCE (lo.leg_memo → l.leg_memo → o.memo → h.memo) — fine
-- as the register's primary display value, but it collapses the
-- leg-vs-header distinction the editor needs:
--
--   * Editor opens edit mode → loads existing legMemos from the
--     DTO. With only the COALESCEd `memo` available, a leg with
--     no per-leg memo loads its HEADER's memo as if it were a
--     leg memo. Re-saving then promotes the header memo into
--     the leg-memo column — silent data corruption.
--   * Split-leg rows in the register display the COALESCEd
--     `memo` → falls back to header memo when leg has none →
--     "the same memo on every leg row" visual bug.
--
-- Two new columns, both with no fallback to the other layer:
--   leg_memo     := COALESCE(lo.leg_memo, l.leg_memo)
--   header_memo  := COALESCE(o.memo, h.memo)
--
-- The existing `memo` column stays untouched for backwards
-- compatibility with consumers that want the full COALESCEd
-- value (single-row register display, etc).
--
-- CREATE OR REPLACE VIEW requires the column list to be a strict
-- extension of the existing shape — same columns in the same
-- order, then any additions at the end. Body rebased on the
-- latest definition (migration 030) so post-030 columns
-- (header.status, cleared_at, cleared_by_user_id, header_id) are
-- preserved.

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
    -- ADR-0025: raw leg-level memo, no header fallback.
    COALESCE(lo.leg_memo, l.leg_memo)                                  AS leg_memo,
    -- ADR-0025: raw header-level memo, no leg fallback.
    COALESCE(o.memo, h.memo)                                           AS header_memo
FROM txn_legs l
JOIN txn_headers h ON h.id = l.header_id
LEFT JOIN txn_header_overrides o ON o.header_id = h.id
LEFT JOIN txn_leg_overrides    lo ON lo.leg_id  = l.id
LEFT JOIN txn_legs other
    ON other.header_id = l.header_id
    AND other.posting_index = l.posting_index
    AND other.id != l.id
LEFT JOIN accounts ca ON ca.id = other.account_id;

ALTER VIEW resolved_transactions SET (security_invoker = true);
