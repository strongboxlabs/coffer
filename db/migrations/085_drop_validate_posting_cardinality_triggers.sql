-- =============================================================================
-- 085 — drop trg_validate_posting_cardinality_insert / _update + function
-- =============================================================================
--
-- Per [ADR-0032](../decisions/0032-triggers-as-last-resort.md), validation
-- invariants live in API code; this trigger family is the second
-- removal (after migration 084's trg_validate_posting_role).
--
-- INVARIANT (ADR-0019):
--     every (header_id, posting_index) has exactly 2 legs at commit.
--
-- The cardinality check (count > 2) is the IMMEDIATE-fail half. The
-- completeness check (count = 1 at commit) is enforced by a separate
-- deferred trigger; that one stays for slice 2.
--
-- AUDIT — every write site already upholds cardinality by construction:
--
--   * InvestmentTransactionsRepository.CreateAsync / .PatchAsync
--     — InvestmentPostings.BuildPostings returns posting specs each
--       containing one Cash leg + one Counterparty leg. The batched
--       TVF insert preserves the pair.
--   * IngestOrchestrator.RunPullAsync — writes exactly 2 legs per
--     posting (the brokerage cash leg + the Uncategorized counterparty).
--   * TransactionsRepository (bank PATCH overrides) — touches amount /
--     memo only; never adds or removes legs.
--   * Importer.Moneydance.Db.TransactionsRepository — constructs
--     symmetric postings via its own BuildPostings helper.
--
-- Raw-SQL writes outside of these paths are prohibited by
-- engineering-standards §3.3. Test fixtures (SyntheticLedger, etc.)
-- INSERT 2 legs at a time and would be obviously wrong if they
-- inserted 3 — API integration tests cover the post-state
-- (e.g. InvestmentTransactionsEndpointsTests asserts on leg counts
-- after Create + PATCH).
-- =============================================================================

DROP TRIGGER IF EXISTS trg_validate_posting_cardinality_insert ON txn_legs;
DROP TRIGGER IF EXISTS trg_validate_posting_cardinality_update ON txn_legs;
DROP FUNCTION IF EXISTS fn_validate_posting_cardinality_stmt();
