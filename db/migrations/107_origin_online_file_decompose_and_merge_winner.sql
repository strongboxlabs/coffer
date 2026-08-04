-- Reshape txn_headers.origin to mean "source mechanism" (manual /
-- online_import / file_import); add provider_key for audit detail
-- and is_merge_winner for the register's merge overlay.
--
-- Why this changes
-- ================
--
-- The old `origin` column conflated two concepts:
--   1. How the row reached Coffer (which ingest mechanism wrote it)
--   2. Where the underlying transaction originated (online feed vs
--      file upload vs typed manually)
--
-- Tens of thousands of rows had `origin='moneydance_import'` which
-- described (1) only — every row that came from the MD JSON bootstrap
-- import — and
-- erased (2). A SimpleFIN-style live-feed row that MD had previously
-- captured looked identical in `origin` to a row the user typed by
-- hand in MD. The register couldn't render a provenance indicator
-- post-accept because the data didn't carry it.
--
-- This mig decomposes `origin` into the user's mental model:
--
--   * manual         — typed entry (Coffer API OR MD pre-online-feed)
--   * online_import  — any live feed: SimpleFIN, MD+ Direct Connect,
--                      OFX online (legacy pre-MD+)
--   * file_import    — any file upload: Coffer's OFX/QFX endpoint,
--                      MD-side QIF, MD-side CSV / text-import
--
-- Per-provider audit detail moves to a new `provider_key` column
-- (TEXT, NULL on manual): simplefin / mdplus / ofx / qif / csv. The
-- bootstrap-import marker stays on the existing (currently unused)
-- `import_source` column: 'moneydance_export' on every MD-bootstrap
-- row, NULL on rows born in Coffer.
--
-- A second column `is_merge_winner` (BOOLEAN, default FALSE) records
-- whether anything was merged INTO this row. Today the loser is
-- already hidden via `is_merged_into`; this denorm lets the register
-- surface a merge-winner overlay without an EXISTS subquery on the
-- hot resolved view. Backfilled from existing is_merged_into data;
-- the merge code path flips it atomically going forward.
--
-- MD-row decomposition rules (verified against a large real-world
-- MD export, with targeted spot-checks against four representative
-- accounts spanning the source mix — bank with online + manual
-- history, an investment with predominantly MD+, a credit card
-- with a CSV-heavy era, and an investment with legacy MD+
-- date-prefix FITIDs):
--
--   qif fields set                          → file_import, qif
--   ol_fi_id = 'md:txtimport'               → file_import, csv
--   ol_fitid_1 LIKE 'md%import:%'           → file_import, csv
--   ol_fitid_1 ~ '^\d{8}:' or '^\d{4}-…'    → online_import, mdplus  (legacy MD+ format —
--                                                                     MD+ ingested these online
--                                                                     and stopped supporting
--                                                                     the date-prefix shape at
--                                                                     some point. Thousands of rows.)
--   ol_fi_id LIKE 'mdplus:%'                → online_import, mdplus
--   ol_fi_id LIKE 'ofx:%'                   → online_import, ofx
--   ol_fitid_1 set, none of above           → online_import, ofx   (assume real OFX FITID)
--   nothing                                 → manual
--
-- Companion code changes (same PR):
--   * IngestOrchestrator (SimpleFIN pull path) writes online_import/simplefin
--   * IngestOrchestrator (OFX file path)       writes file_import/ofx
--   * TransactionsRepository.PatchAsync's merge step flips
--     winner.IsMergeWinner=true atomically with loser.IsMergedInto
--   * Importer.Moneydance.TransactionMapper applies the decomposition
--     rules above and stamps import_source='moneydance_export'
--   * resolved_transactions view projects provider_key + is_merge_winner

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Add the new columns BEFORE we change the CHECK or backfill anything.
--    NOT NULL DEFAULT FALSE on is_merge_winner so the column appears
--    correctly on all existing rows.
-- ---------------------------------------------------------------------------
ALTER TABLE txn_headers
    ADD COLUMN provider_key TEXT,
    ADD COLUMN is_merge_winner BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN txn_headers.provider_key IS
    'Specific ingest provider that wrote this row (audit detail). '
    'Values: simplefin / mdplus / ofx / qif / csv. NULL when '
    'origin=manual. Distinct from origin (which is the icon-level '
    'mechanism — manual / online_import / file_import). Mig 107.';

COMMENT ON COLUMN txn_headers.is_merge_winner IS
    'TRUE when at least one other row has is_merged_into pointing '
    'at this row. Maintained atomically with the merge mutation in '
    'TransactionsRepository.PatchAsync. Surfaces in the register as '
    'an overlay icon on the row''s provenance indicator (mig 107). '
    'Monotonic: there is no unmerge surface today, so once TRUE the '
    'flag stays TRUE.';

-- ---------------------------------------------------------------------------
-- 2. Widen the origin CHECK to accept BOTH the old values (so the
--    backfill UPDATE doesn't fail) AND the new ones. We narrow the
--    enum again at the end of this mig.
-- ---------------------------------------------------------------------------
ALTER TABLE txn_headers DROP CONSTRAINT txn_headers_origin_check;
ALTER TABLE txn_headers ADD CONSTRAINT txn_headers_origin_check
    CHECK (origin = ANY (ARRAY[
        'manual',
        'online_import',
        'file_import',
        -- transitional (dropped at end of mig):
        'simplefin', 'moneydance_import', 'ofx_import', 'csv_import'
    ]));

-- ---------------------------------------------------------------------------
-- 3. Backfill is_merge_winner from existing is_merged_into pointers.
--    Distinct because one winner can have multiple losers — we only
--    need to flag the winner once.
-- ---------------------------------------------------------------------------
UPDATE txn_headers w
   SET is_merge_winner = TRUE
  FROM (SELECT DISTINCT is_merged_into AS winner_id
          FROM txn_headers
         WHERE is_merged_into IS NOT NULL) m
 WHERE w.id = m.winner_id;

-- ---------------------------------------------------------------------------
-- 4. Backfill simplefin → online_import + provider_key.
-- ---------------------------------------------------------------------------
UPDATE txn_headers
   SET origin = 'online_import',
       provider_key = 'simplefin'
 WHERE origin = 'simplefin';

-- ---------------------------------------------------------------------------
-- 5. Backfill ofx_import (PR #151's OFX file importer) → file_import + ofx.
-- ---------------------------------------------------------------------------
UPDATE txn_headers
   SET origin = 'file_import',
       provider_key = 'ofx'
 WHERE origin = 'ofx_import';

-- ---------------------------------------------------------------------------
-- 6. Backfill csv_import (reserved but never populated) → file_import + csv.
--    Defensive — schema allowed the value, no rows actually have it.
-- ---------------------------------------------------------------------------
UPDATE txn_headers
   SET origin = 'file_import',
       provider_key = 'csv'
 WHERE origin = 'csv_import';

-- ---------------------------------------------------------------------------
-- 7. MD-import decomposition. Order matters — more specific
--    discriminators (explicit md:txtimport marker, CSV FITID
--    prefixes, legacy MD+ date-prefix shape) run BEFORE the
--    generic mdplus/ofx fi_id prefix rules so the specific signal
--    wins. After this block every moneydance_import row has
--    been re-tagged (or trips the defensive RAISE in step 9).
--    QIF detection is NOT in the backfill — see 7a.
-- ---------------------------------------------------------------------------

-- 7a. QIF detection is INTENTIONALLY ABSENT from the backfill.
--     The importer's DecomposeOrigin reads MdTxn.QifInvstAction /
--     QifOrigTxn / QifSn directly and correctly tags
--     file_import / qif on fresh imports. But those JSON fields
--     are never projected to txn_headers columns (intentional —
--     QIF metadata isn't useful at runtime), so the backfill has
--     no DB-level QIF signal to read.
--
--     An earlier draft tried `online_match_orig_id IS NOT NULL`
--     as a QIF proxy, but that column is set on EVERY OL-matched
--     row (not just QIF — MD's `ol.orig-txn` projection always
--     carries the bank-original JSON blob on online-matched rows),
--     so it grabbed everything and shadowed every rule below.
--
--     Net effect on backfill: thousands of QIF-only rows (no ol_fitid,
--     only qif_* in MD JSON) classify as `origin='manual'`. They
--     are not visibly distinct from truly-manual rows in the
--     register. To recover the QIF tagging, re-run the MD
--     importer over the original JSON — the per-row UPSERT path
--     re-stamps provider_key='qif' on rows the mapper detects.
--     See ADR-0035 §"Migration safety" for the trade-off.

-- 7b. CSV / text-import via the modern MD marker.
UPDATE txn_headers
   SET origin = 'file_import',
       provider_key = 'csv'
 WHERE origin = 'moneydance_import'
   AND online_match_fi_id = 'md:txtimport';

-- 7c. CSV / text-import via MD's modern synthesized FITID prefix
--     (mdtxtimport: / mdcsvimport: / mdqifimport:).
UPDATE txn_headers
   SET origin = 'file_import',
       provider_key = 'csv'
 WHERE origin = 'moneydance_import'
   AND (online_match_fitid LIKE 'mdtxtimport:%'
        OR online_match_fitid LIKE 'mdcsvimport:%'
        OR online_match_fitid LIKE 'mdqifimport:%');

-- 7d. Legacy MD+ format: FITID starts with a date prefix
--     (YYYYMMDD:... or YYYY-MM-DD...) and there's no ol_fi_id.
--     MD+ used this format for online-fetched rows during an
--     earlier era and stopped supporting it later; the rows
--     stayed in MD's database with their legacy shape. They are
--     online_import / mdplus despite the absent ol_fi_id prefix.
--     Per user feedback during ADR-0035 — earlier draft had these
--     as file_import/csv which mis-classified thousands of rows
--     concentrated on two accounts in the user's data.
UPDATE txn_headers
   SET origin = 'online_import',
       provider_key = 'mdplus'
 WHERE origin = 'moneydance_import'
   AND online_match_fi_id IS NULL
   AND online_match_fitid IS NOT NULL
   AND (online_match_fitid ~ '^[0-9]{8}:'
        OR online_match_fitid ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}');

-- 7e. MD+ Direct Connect → online_import / mdplus.
UPDATE txn_headers
   SET origin = 'online_import',
       provider_key = 'mdplus'
 WHERE origin = 'moneydance_import'
   AND online_match_fi_id LIKE 'mdplus:%';

-- 7f. OFX online (recognized FI) → online_import / ofx.
UPDATE txn_headers
   SET origin = 'online_import',
       provider_key = 'ofx'
 WHERE origin = 'moneydance_import'
   AND online_match_fi_id LIKE 'ofx:%';

-- 7g. Legacy OFX (no fi_id, but has a non-MD-synthesized FITID) →
--     assume real OFX server. online_import / ofx.
UPDATE txn_headers
   SET origin = 'online_import',
       provider_key = 'ofx'
 WHERE origin = 'moneydance_import'
   AND online_match_fitid IS NOT NULL;

-- 7h. Everything else from MD with no ingest signal → manual.
--     These are user-typed entries in MD (or imports MD couldn't
--     tag). Lossy on the "QFX-file MD couldn't recognize" case but
--     unavoidable without an MD JSON discriminator.
UPDATE txn_headers
   SET origin = 'manual',
       provider_key = NULL
 WHERE origin = 'moneydance_import';

-- ---------------------------------------------------------------------------
-- 8. Bootstrap marker — every row that originally came in via the MD
--    JSON dump gets import_source='moneydance_export'. Detected by
--    "was previously origin='moneydance_import'" — by this point all
--    such rows have been re-tagged but they're identifiable by
--    NOT having a feed_connection_id chain + having either
--    online_match_* metadata OR being origin='manual' with no
--    matching Coffer-native paper trail. Simpler: re-derive from
--    the columns the MD importer always sets (created_at predates
--    the API era).
--
--    For the backfill we use a heuristic: any row whose created_at
--    is OLDER than the SimpleFIN deployment (first sync_run on
--    this DB) AND origin in (manual, online_import, file_import) AND
--    NOT in the SimpleFIN cohort (provider_key != 'simplefin') is
--    an MD-bootstrap row. This is approximate but safe — a manual
--    row created in Coffer later won't accidentally be tagged.
--
--    Going forward the importer writes import_source directly.
-- ---------------------------------------------------------------------------
UPDATE txn_headers
   SET import_source = 'moneydance_export'
 WHERE import_source IS NULL
   AND (provider_key != 'simplefin' OR provider_key IS NULL)
   AND created_at < COALESCE(
       (SELECT MIN(started_at) FROM sync_runs WHERE feed_connection_id IS NOT NULL),
       '9999-12-31'::timestamptz);

-- ---------------------------------------------------------------------------
-- 9. Narrow the origin CHECK to the final 3 values. Defensive
--    assertion catches any row we forgot to re-tag.
-- ---------------------------------------------------------------------------
DO $$
DECLARE n INTEGER;
BEGIN
    SELECT COUNT(*) INTO n FROM txn_headers
     WHERE origin NOT IN ('manual', 'online_import', 'file_import');
    IF n > 0 THEN
        RAISE EXCEPTION 'mig 107: % rows still carry a transitional origin value — backfill missed a case', n;
    END IF;
END $$;

ALTER TABLE txn_headers DROP CONSTRAINT txn_headers_origin_check;
ALTER TABLE txn_headers ADD CONSTRAINT txn_headers_origin_check
    CHECK (origin = ANY (ARRAY['manual', 'online_import', 'file_import']));

-- ---------------------------------------------------------------------------
-- 10. provider_key invariant — NULL iff origin='manual'. Cheap to
--     enforce; catches importer / orchestrator bugs at INSERT time.
-- ---------------------------------------------------------------------------
ALTER TABLE txn_headers ADD CONSTRAINT ck_txn_headers_provider_key_iff_not_manual
    CHECK ((origin = 'manual') = (provider_key IS NULL));

COMMENT ON COLUMN txn_headers.origin IS
    'Source mechanism (icon-level grouping, mig 107). One of: '
    '`manual` (typed entry), `online_import` (live feed — SimpleFIN, '
    'MD+, OFX online), `file_import` (file upload — OFX/QFX, CSV, '
    'QIF). Per-provider detail lives in provider_key. Drives the '
    'register provenance icon.';

-- ---------------------------------------------------------------------------
-- 11. Refresh resolved_transactions view to project the new columns.
--     CREATE OR REPLACE requires strict column-list extension; the new
--     three columns (provider_key / is_merge_winner / import_source)
--     append at the end. Body otherwise identical to mig 100.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE VIEW resolved_transactions AS
SELECT l.id,
    l.account_id,
    COALESCE(o.payee, h.payee) AS payee,
    COALESCE(lo.leg_memo, l.leg_memo, o.memo, h.memo) AS memo,
    COALESCE(lo.amount, l.amount) AS amount,
    COALESCE(o.posted_at, h.posted_at) AS posted_at,
    COALESCE(o.transacted_at, h.transacted_at) AS transacted_at,
    h.status,
    COALESCE(o.is_hidden, h.is_hidden, false) AS is_hidden,
    o.header_id IS NOT NULL OR lo.leg_id IS NOT NULL AS has_overrides,
    thab.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    h.action AS investment_action,
    h.external_id,
    l.created_at,
    COALESCE(o.check_number, h.check_number) AS check_number,
    other.id AS counterparty_id,
        CASE
            WHEN (EXISTS ( SELECT 1
               FROM txn_legs g
              WHERE g.header_id = h.id AND g.posting_index > 0)) THEN h.id
            ELSE NULL::uuid
        END AS txn_group_id,
    l.posting_index AS leg_index,
    other.account_id AS counterparty_account_id,
    account_path(other.account_id) AS counterparty_account_name,
    ca.account_type AS counterparty_account_type,
    COALESCE(ARRAY( SELECT tg.name
           FROM txn_header_tags tt
             JOIN tags tg ON tg.id = tt.tag_id
          WHERE tt.header_id = h.id
          ORDER BY tg.name), ARRAY[]::text[]) AS tags,
    h.id AS header_id,
    h.cleared_at,
    h.cleared_by_user_id,
    COALESCE(lo.leg_memo, l.leg_memo) AS leg_memo,
    COALESCE(o.memo, h.memo) AS header_memo,
    h.online_match_fitid,
    h.online_match_fi_id,
    h.online_match_status,
    h.online_match_type,
    h.online_match_orig_id,
    h.needs_review,
    COALESCE(l.security_id, other.security_id) AS security_id,
    s.ticker AS security_ticker,
    s.name AS security_name,
    COALESCE(l.quantity, other.quantity) AS quantity,
    COALESCE(l.unit_price, other.unit_price) AS unit_price,
    l.posting_role,
    h.ingest_action_hint,
    h.ingest_security_id,
    h.provider_raw_payload,
    h.seq AS header_seq,
    thab.net_amount AS header_account_net_amount,
    -- Mig 107: register provenance + merge-winner overlay.
    h.provider_key,
    h.is_merge_winner,
    h.import_source
   FROM txn_legs l
     JOIN txn_headers h ON h.id = l.header_id
     LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
     LEFT JOIN txn_legs other ON other.header_id = l.header_id AND other.posting_index = l.posting_index AND other.id <> l.id
     LEFT JOIN accounts ca ON ca.id = other.account_id
     LEFT JOIN securities s ON s.id = COALESCE(l.security_id, other.security_id)
     LEFT JOIN txn_header_account_balances thab
            ON thab.header_id = h.id AND thab.account_id = l.account_id;

ALTER VIEW resolved_transactions SET (security_invoker = true);
GRANT SELECT ON resolved_transactions TO coffer_app;
GRANT ALL    ON resolved_transactions TO coffer_service;

COMMIT;
