-- 044_drop_vestigial_tables.sql
--
-- Drop four tables that no production code reads or writes anymore.
-- Audit run before this migration (real dev DB):
--
--   pending_transactions   0 rows, 0 source-code SQL refs.
--       Slice 2c moved bank-feed staging from `pending_transactions`
--       onto `txn_headers` with is_pending + needs_review directly.
--       follow-ups.md flagged this for removal after a verification
--       window; the window has passed (multiple slices shipped since).
--
--   merge_candidates       0 rows, 0 source-code SQL refs.
--       Pre-2c.6 auto-merge pipeline staging table. Slice 2c.6d
--       re-implemented merge candidates as a server-side function
--       reading `txn_headers` directly (no staging). The DTO name
--       MergeCandidate in TransactionWriteDtos is unrelated to this
--       table.
--
--   merge_rules            1 stale config row, 0 source-code refs.
--       Pre-2c.6 auto-merge pipeline config. The slice 2c.6
--       hand-driven merge flow has no config knobs; if rule-driven
--       auto-merge returns later it'll get a fresh schema designed
--       against the new pipeline (txn_headers, not pending_transactions).
--
--   transaction_rules      0 rows, 0 source-code refs.
--       Reserved column shape from the original Phase 0 plan for
--       payee-substring → category auto-categorisation. The
--       follow-up "Rule-based auto-categorization on sync" is still
--       open (Phase 5+), but the future schema will be designed
--       against the current sync pipeline, not this 2024-era table.
--
-- All four have zero FK references pointing INTO them from tables
-- we keep (verified via pg_constraint scan), so DROP is unconditional.

BEGIN;

DROP TABLE IF EXISTS pending_transactions;
DROP TABLE IF EXISTS merge_candidates;
DROP TABLE IF EXISTS merge_rules;
DROP TABLE IF EXISTS transaction_rules;

COMMIT;
