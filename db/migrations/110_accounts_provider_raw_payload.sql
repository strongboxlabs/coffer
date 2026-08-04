-- =============================================================================
-- 110 — accounts.provider_raw_payload (ADR-0035 §3 extended to accounts)
-- =============================================================================
--
-- The bootstrap-vs-classification audit (mig 109 / ADR-0035 §3) locked
-- in the rule: provider data lands in the database verbatim;
-- classification is derived from it. We applied it to `txn_headers` but
-- not yet to `accounts`.
--
-- The motivating gap: the per-txn `ol_fi_id` field looks identical for
-- online OFX feeds and QFX file imports — both come out as
-- `ofx:<INSTITUTION>:<acct>`. The actual discriminator lives on the MD
-- `acct` object, NOT on the txn:
--
--   * `olbfi` set (e.g. `:ofx.example-broker.com:0000`) → the account is
--     configured for live online OFX. Per-txn rows with `ol_fi_id ofx:`
--     prefix are `online_import / ofx`.
--   * `ofx_import_acct_num` set → the account is set up for QFX file
--     imports. Per-txn rows with the same shape are `file_import / ofx`.
--
-- Currently neither field is persisted; the importer reads them
-- transiently and discards. Same lossy-import bug we just fixed for
-- txns. Fix: persist the per-account MD JSON verbatim, drive the
-- classifier off it, never depend on the source file again.
--
-- This migration adds the column. The importer change (next commit)
-- writes it on every account UPSERT. After re-import, every account
-- has provider_raw_payload populated and the classifier can split
-- `ofx:` per-txn rows into online vs file by reading the account's
-- payload.
-- =============================================================================

ALTER TABLE accounts
    ADD COLUMN provider_raw_payload JSONB;

COMMENT ON COLUMN accounts.provider_raw_payload IS
    'Verbatim per-row provider data for this account, captured at '
    'import time (MD ``acct`` JSON for MD-bootstrap accounts; future '
    'SimpleFIN / OFX-direct importers will populate it analogously). '
    'Per ADR-0035 §3, classification rules can read this column '
    'directly — no need for the source file. NULL on Coffer-native '
    'accounts created via the API.';
