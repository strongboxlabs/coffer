-- Needs-review flag on txn_headers (Phase 5 slice 2c).
--
-- Modern bank-feed aggregators (Monarch, YNAB, Copilot) land
-- bank-posted feed transactions directly into the register and
-- surface them with a "needs review" flag rather than gating them
-- behind a per-account staging tab (the MD / Quicken pattern).
-- ADR-0021 visual treatment lifts the concept verbatim — flagged
-- rows stay in the register at their date-sorted position with a
-- distinct visual treatment until the user approves.
--
-- Bank-pending transactions (SimpleFIN `pending: true`) still land
-- in `pending_transactions` because they're not real yet — the
-- bank itself hasn't cleared them. On a later sync that returns
-- the same FITID with `pending: false`, the sync service promotes
-- the row out of `pending_transactions` and into `txn_headers`
-- with `needs_review = true`.
--
-- New column is NOT NULL DEFAULT FALSE so existing rows (manual,
-- MD-imported, OFX-imported) stay un-flagged — they're the user's
-- prior register state, not new bank-feed events the user hasn't
-- seen. Only the sync service writes TRUE on insert.

ALTER TABLE txn_headers
    ADD COLUMN needs_review BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN txn_headers.needs_review IS
    'TRUE on rows the SimpleFIN sync service freshly inserted from a '
    'bank-posted feed item (slice 2c). The register renders these '
    'with a visual flag until the user clicks Approve, which clears '
    'the flag. Importer paths (Moneydance, OFX, manual) write FALSE '
    'because those rows are already user-confirmed history at the '
    'moment of insert.';

-- Partial index: the "review-only" register filter and the inbox
-- count badge both predicate on `needs_review = TRUE`. Index only
-- the TRUE rows because the bulk of the table is FALSE (every
-- pre-existing row + every user-approved row) — a full index
-- would waste both space and update bandwidth.
CREATE INDEX idx_txn_headers_needs_review
    ON txn_headers (ledger_id)
    WHERE needs_review;

-- ---------------------------------------------------------------------------
-- Rebase resolved_transactions on the new column. CREATE OR REPLACE
-- VIEW requires the column list to be a strict extension — same
-- columns in the same order, then any additions at the end. Body
-- rebased on migration 034 (latest definition).
-- ---------------------------------------------------------------------------
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
    -- Slice 2c: feed-sourced rows the user hasn't approved yet.
    -- Header-projected directly; no override layer because the
    -- bit is a workflow flag, not user-editable content.
    h.needs_review                                                     AS needs_review
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
