-- =============================================================================
-- 076 — txn_headers ingest hints (ADR-0031 Phase 3c)
-- =============================================================================
--
-- Adds two NULL-able columns to txn_headers so the orchestrator can
-- persist the SimpleFIN classifier's outputs alongside a sync-imported
-- row without flipping the row into investment-shape at sync time
-- (which would require populating the full posting-role × shares ×
-- unit_price matrix per ADR-0029, which sync can't do — SimpleFIN's
-- wire format provides neither shares nor unit price).
--
-- The investment editor's `mode='edit'` flow (Phase 3d) reads these
-- two hints on open + pre-fills the action picker and the security
-- typeahead. When the user saves via /investment-transactions, the
-- existing ADR-0029 create path runs and converts the row into a
-- proper investment-shape header. The hint columns stay populated as
-- audit metadata.
--
-- Why two columns instead of a sidecar table:
--   * They're 1:1 with the txn_header row (no fan-out)
--   * The editor's existing pre-fill query already joins
--     txn_headers; one less join
--   * RLS is inherited from txn_headers (no new policy needed)
--
-- Why NOT relax fn_validate_posting_role for needs_review rows:
--   * Per ADR-0019/0029 the posting_role invariant is load-bearing —
--     it backs the cardinality + completeness checks. Relaxing it on
--     a per-row flag would let an invalid state persist in the DB,
--     which the user-facing UI couldn't reliably show or fix.
-- =============================================================================

-- The existing `action` column on txn_headers uses this CHECK
-- (migration 060, ADR-0027 catalog):
--   action IN ('buy','buyx','sell','sellx','dividend_cash',
--              'dividend_reinvest','divx','transfer','misc') OR NULL
-- The hint column uses the same vocabulary so the editor can flip
-- straight from hint → action without translation.
ALTER TABLE txn_headers
    ADD COLUMN ingest_action_hint TEXT NULL,
    ADD COLUMN ingest_security_id UUID NULL;

ALTER TABLE txn_headers
    ADD CONSTRAINT ck_txn_headers_ingest_action_hint
    CHECK (ingest_action_hint IS NULL OR ingest_action_hint = ANY (ARRAY[
        'buy', 'buyx', 'sell', 'sellx',
        'dividend_cash', 'dividend_reinvest', 'divx',
        'transfer', 'misc'
    ]));

-- Composite FK to securities (per the migration 049 ledger-isolation
-- pattern). ON DELETE SET NULL because the hint is metadata; the user
-- can re-link the security via the editor without the synced row
-- disappearing.
ALTER TABLE txn_headers
    ADD CONSTRAINT fk_txn_headers_ingest_security
    FOREIGN KEY (ingest_security_id, ledger_id)
    REFERENCES securities (id, ledger_id)
    ON DELETE SET NULL;

-- Partial index for the editor's pre-fill lookup: only the synced
-- rows that actually carry a hint. Most rows have neither column
-- populated (manual entries, MD-imported, OFX-imported); the partial
-- index keeps the btree small.
CREATE INDEX idx_txn_headers_ingest_hints
    ON txn_headers (ledger_id, ingest_security_id)
    WHERE ingest_security_id IS NOT NULL;

COMMENT ON COLUMN txn_headers.ingest_action_hint IS 'ADR-0031 Phase 3c: provider-classifier output (action catalog per ADR-0027). Set by the orchestrator brokerage branch when sync detects an investment-shape transaction; null otherwise. Editor uses this to pre-fill the action picker on review.';

COMMENT ON COLUMN txn_headers.ingest_security_id IS 'ADR-0031 Phase 3c: provider-classifier output, resolved through provider_security_mappings. NULL when the classifier could not extract a ticker OR the user has not mapped the ticker yet. Composite FK matches the migration 049 pattern.';
