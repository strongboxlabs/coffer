-- SimpleFIN ingest: external_id becomes the canonical per-row identifier;
-- online_match_* reverts to OFX-protocol-only.
--
-- Bug context. The bank `DELETE /transactions/{id}` endpoint picks
-- hard-delete vs soft-hide based on `external_id IS NULL`
-- (TransactionsRepository.DeleteAsync). For Moneydance-imported rows
-- that predicate works — every MD row has external_id set to MD's
-- txnid. For SimpleFIN-synced rows it didn't: SimpleFIN's transaction
-- id was being parked in `online_match_fitid` while `external_id`
-- stayed NULL. So DELETE on a SimpleFIN row went down the
-- hard-delete branch, physically removing the row. The very next
-- sync didn't recognise the FITID (no row to match against),
-- treated it as new, re-inserted it. User reported a deleted
-- "Balance" row reappearing after sync — exactly this path.
--
-- Root cause is category confusion. `online_match_fitid` +
-- `online_match_fi_id` were introduced (mig 034) as OFX-protocol
-- identifiers — preserved by the MD importer from rows MD had
-- previously bank-feed-matched, and intended to be written natively
-- by future OFX/QFX direct importers. SimpleFIN ids are NOT OFX
-- FITIDs — they're SimpleFIN-proprietary strings (`TRN-…`).
-- SimpleFIN's `org_id` is NOT an OFX FI_ID either. Mig 034's
-- forward-looking note about "SimpleFIN sync dedup via this column"
-- baked in that confusion.
--
-- This migration:
--   1. Backfills `external_id` on every existing SimpleFIN row by
--      copying from `online_match_fitid`. Idempotent — only
--      touches rows where external_id is NULL.
--   2. Clears `online_match_fitid` + `online_match_fi_id` on those
--      same rows. SimpleFIN values were never OFX values; future
--      OFX/QFX importers will write the real OFX values into these
--      columns directly.
--   3. Adds CHECK (is_user_defined OR external_id IS NOT NULL).
--      Provider-agnostic invariant: every non-manual row must
--      carry an external_id. The next ingest writer can't regress
--      this without tripping the constraint at INSERT time.
--
-- Companion code changes:
--   - IngestOrchestrator writes SimpleFIN id → external_id and
--     dedups by (ledger_id, origin, external_id).
--   - mig 034's column comments updated below to remove the
--     "future SimpleFIN dedup" forward note now that SimpleFIN
--     uses external_id instead.

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Backfill external_id from online_match_fitid for SimpleFIN rows.
-- ---------------------------------------------------------------------------
UPDATE txn_headers
   SET external_id = online_match_fitid
 WHERE origin = 'simplefin'
   AND external_id IS NULL
   AND online_match_fitid IS NOT NULL;

-- ---------------------------------------------------------------------------
-- 2. Drop the misclassified OFX fields on SimpleFIN rows. SimpleFIN
--    has no OFX FITID/FI_ID concept, so these were never meaningful.
--    Future OFX/QFX direct importers will populate them natively
--    from the OFX wire fields.
-- ---------------------------------------------------------------------------
UPDATE txn_headers
   SET online_match_fitid = NULL,
       online_match_fi_id = NULL
 WHERE origin = 'simplefin';

-- ---------------------------------------------------------------------------
-- 3. Provider-agnostic NOT-NULL invariant on external_id for every
--    non-manual row. is_user_defined is the existing column that
--    cleanly partitions manual (t) vs every feed/import path (f).
--    Stated as CHECK rather than NOT NULL because manual rows
--    legitimately have external_id NULL.
-- ---------------------------------------------------------------------------
ALTER TABLE txn_headers
    ADD CONSTRAINT ck_txn_headers_external_id_for_non_user_defined
    CHECK (is_user_defined OR external_id IS NOT NULL);

-- ---------------------------------------------------------------------------
-- 4. Refresh mig 034's column comments so the next reader doesn't
--    repeat the original mis-conflation. The OFX-state columns are
--    now exactly what their name says — OFX-protocol identifiers,
--    written by the MD importer (preserving OFX state) and by
--    future OFX/QFX direct importers. NOT a SimpleFIN dedup
--    surface — SimpleFIN uses external_id.
-- ---------------------------------------------------------------------------
COMMENT ON COLUMN txn_headers.external_id IS
    'Universal per-provider stable identifier for the row. Set by '
    'every ingest path (moneydance_import: MD txnid; simplefin: '
    'SimpleFIN transaction id; ofx/qfx: FITID or composite). NULL '
    'only on manual rows (is_user_defined = TRUE). Primary dedup '
    'key for re-sync / re-import — soft-hide on DELETE preserves '
    'this so the next sync recognises the row.';

COMMENT ON COLUMN txn_headers.online_match_fitid IS
    'OFX <FITID> — bank''s per-transaction unique id under the OFX '
    'protocol. Populated by the MD importer (preserving MD''s '
    'recorded OFX match state) and by future OFX/QFX direct '
    'importers from the wire fields. NOT written by SimpleFIN — '
    'SimpleFIN ids live in external_id (mig 105).';

COMMENT ON COLUMN txn_headers.online_match_fi_id IS
    'OFX FI id — identifies which financial institution issued this '
    'transaction under the OFX protocol. Composite with '
    'online_match_fitid for OFX-global uniqueness. NOT written by '
    'SimpleFIN — SimpleFIN''s org_id is not an OFX FI_ID and is '
    'recoverable via feed_connections (mig 105).';

COMMIT;
