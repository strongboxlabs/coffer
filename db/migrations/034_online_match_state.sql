-- Preserve OFX-online match state on import (fidelity audit §4 + §6).
--
-- Five new columns on `txn_headers`, all nullable TEXT. Carry the
-- OFX-level identifiers + MD's tracked match-state through from the
-- importer so we can:
--
--   1. Dedupe incoming SimpleFIN (Phase 5+) sync items against rows
--      already in the register — composite key (fi_id, fitid) per the
--      OFX spec (FITID uniqueness is per-FI, not global).
--
--   2. Honour the user's pre-existing bank-feed work — match-status /
--      match-type / orig-id together encode "this row was matched in
--      MD, confirmed/rejected by the user, by which heuristic, against
--      which feed item." Dropping that on import would make every
--      future SimpleFIN sync re-prompt the user about matches they
--      already resolved (30,110 rows in the canonical export, ~72%).
--
-- Column mapping from MD JSON → schema:
--   ol_fitid_1      → online_match_fitid
--   ol_fi_id        → online_match_fi_id
--   ol.match-status → online_match_status
--   ol.match-type   → online_match_type
--   ol.orig-txn     → online_match_orig_id
--
-- CSV-source imports leave all five NULL; the columns are
-- source-agnostic but only OFX-style protocols carry the data.

-- `online_match_status` was created in migration 022 with a
-- forward-looking CHECK ('unmatched' / 'auto_matched' / 'user_matched')
-- that doesn't match MD's actual on-disk vocabulary. Drop the
-- CHECK so the importer can preserve whatever MD writes verbatim;
-- the value space stays implicit (the importer is the only writer
-- today). When SimpleFIN sync ships we can re-introduce a CHECK
-- with the unified vocabulary across both source paths.
ALTER TABLE txn_headers
    DROP CONSTRAINT IF EXISTS txn_headers_online_match_status_check;

ALTER TABLE txn_headers
    ADD COLUMN online_match_fitid   TEXT,
    ADD COLUMN online_match_fi_id   TEXT,
    ADD COLUMN online_match_type    TEXT,
    ADD COLUMN online_match_orig_id TEXT;

COMMENT ON COLUMN txn_headers.online_match_fitid IS
    'OFX <FITID> — bank''s per-transaction unique id. Unique only '
    'within one financial institution; use (ledger_id, online_match_fi_id, '
    'online_match_fitid) as the dedup key for incoming feed items.';

COMMENT ON COLUMN txn_headers.online_match_fi_id IS
    'OFX FI id — identifies which financial institution issued this '
    'transaction. Composite with online_match_fitid for global uniqueness '
    'across multiple connected banks.';

COMMENT ON COLUMN txn_headers.online_match_status IS
    'MD''s lifecycle code for the online match (e.g. unmatched / matched / '
    'confirmed / rejected). Preserves the user''s years of bank-feed '
    'review work so future SimpleFIN sync does not re-prompt on resolved '
    'matches.';

COMMENT ON COLUMN txn_headers.online_match_type IS
    'How the match was determined: exact FITID round-trip (high confidence), '
    'heuristic on amount+date (lower), or manual user pairing. Lets a '
    'future re-match UI sort by confidence.';

COMMENT ON COLUMN txn_headers.online_match_orig_id IS
    'Pointer to the original feed-item id this row was matched against — '
    'the audit trail linking this register row back to the OFX message '
    'that produced it.';

-- Partial index — the SimpleFIN sync dedup query reads
-- (ledger_id, online_match_fi_id, online_match_fitid) for every
-- incoming feed item. Partial because most rows are CSV / manual /
-- non-online and NULL on these columns — no point indexing the bulk
-- of the table when only ~72% of rows in a typical MD-sourced
-- ledger carry FITIDs.
CREATE INDEX idx_txn_headers_online_match_lookup
    ON txn_headers (ledger_id, online_match_fi_id, online_match_fitid)
    WHERE online_match_fitid IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Rebase resolved_transactions on the new columns. Each prior view
-- migration adds at the END of the SELECT list (CREATE OR REPLACE
-- VIEW requires a strict-extension column list). Body kept identical
-- to migration 032 except for the trailing additions.
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
    -- Migration 034: OFX match state — projected straight from the
    -- header (no override layer; these are feed-sourced facts the
    -- user doesn't edit).
    h.online_match_fitid                                               AS online_match_fitid,
    h.online_match_fi_id                                               AS online_match_fi_id,
    h.online_match_status                                              AS online_match_status,
    h.online_match_type                                                AS online_match_type,
    h.online_match_orig_id                                             AS online_match_orig_id
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
