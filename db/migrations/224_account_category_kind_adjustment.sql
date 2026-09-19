-- =============================================================================
-- 224 — a third category_kind: 'adjustment', for entries that are neither
--       income nor expense.
-- =============================================================================
--
-- ADR-0017 owns this column and is amended by this migration. ADR-0099 D1a is the
-- budget-side consumer.
--
-- WHY A THIRD VALUE. A VALUATION ADJUSTMENT — marking a house up, or reconciling a
-- retirement balance to a new statement figure — moves an asset's value without any
-- money changing hands. It is neither income nor expense. Until now the column could
-- not say so, so such entries were booked into an ordinary expense category, where
-- they read as NEGATIVE SPENDING.
--
-- That is not hypothetical. A single +42,000 markup on a property, credited to an
-- expense category, made one month's total spend compute to -10,929.49 against a
-- true +31,070.51 — the sign of the largest number on the screen, inverted, with
-- nothing red anywhere. Across a real ledger's full history the same shape put 14
-- separate months into negative spending and two entire calendar years into negative
-- INCOME.
--
-- WHY THIS IS ENOUGH ON THE REPORTING SIDE. ReportingRepository.SummarizeAsync
-- selects category postings through an explicit allow-list per measure —
-- Spending => ['expense'], Income => ['income'], Net => ['income','expense'] — so a
-- kind that is not in any list falls out of all three measures with NO code change.
-- That is the whole point of choosing a new kind over a per-category exclusion flag,
-- which would have had to be respected explicitly at every call site.
--
--     WARNING for whoever touches that switch next: Value(measure, kind, sum) takes
--     a `kind` argument it never reads. The signature reads as though per-kind
--     normalisation lives there; it does not, the switch is on the MEASURE alone.
--     Adding 'adjustment' to the Net array therefore yields -sum — the expense
--     treatment — and a house markup would start reducing net savings.
--
-- WHAT THIS MIGRATION DOES NOT DO. It does not reclassify a single existing row.
-- Widening a CHECK is not a data migration, and deciding which of a ledger's
-- categories are really adjustments is a judgement about that ledger's history that
-- belongs to its owner, not to a sweep. Existing installs are byte-identical after
-- this runs; the only change is what the column will now ACCEPT.
--
-- WHY THE DROP IS BARE, WITH NO `IF EXISTS`. The constraint was born inline at
-- 007_phase2_schema.sql:44-45, so Postgres auto-named it and the literal
-- `accounts_category_kind_check` appears in ZERO other repo files. The name is
-- deterministic (`<table>_<column>_check`), but its absence from the codebase
-- invites the `IF EXISTS` habit, and here that habit is actively dangerous: if the
-- name differed on any target install, `IF EXISTS` would silently no-op, this script
-- would journal as APPLIED, the two-value CHECK would survive, and the database
-- would then reject 'adjustment' at runtime on an install the migration claims it
-- upgraded. A bare DROP fails loudly instead, which is the correct outcome.
--
-- (Contrast 034_online_match_state.sql:36, which does use IF EXISTS — it drops
-- WITHOUT re-adding, a different job with a different failure mode.)
--
-- The constraint is re-added under the SAME name so future greps, drift snapshots
-- and any later widening all continue to see one stable identifier.
--
-- `accounts_category_kind_consistent` (007:49-53) is deliberately untouched: its
-- rule — category_kind is set if and only if account_type = 'category' — is
-- orthogonal to which values are legal, and it needs no change for a third one.
-- =============================================================================

BEGIN;

ALTER TABLE accounts
    DROP CONSTRAINT accounts_category_kind_check;

ALTER TABLE accounts
    ADD CONSTRAINT accounts_category_kind_check
    CHECK (category_kind IS NULL
           OR category_kind IN ('income', 'expense', 'adjustment'));

COMMIT;
