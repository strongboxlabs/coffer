-- =============================================================================
-- 066 — Fix posting-completeness trigger to use a per-row indexed lookup
-- =============================================================================
--
-- Migration 065's DEFERRED constraint trigger fn_validate_posting_completeness
-- did a full-table scan + GROUP BY on every invocation. Constraint triggers
-- in PostgreSQL fire per-row at COMMIT, so on a 165k-leg import the function
-- ran 165k times, each doing a full scan — the import timed out before
-- the constraint check could finish.
--
-- This migration replaces the function with a per-row indexed lookup that
-- only examines the leg's own (header_id, posting_index). The composite
-- uq_txn_legs_posting index makes each call O(log N). Total cost on a
-- 165k-row import drops from O(N²) to O(N log N).
--
-- Semantics also clarified: a posting that ends the transaction with
-- 0 legs (fully deleted) is fine. Only 1-leg leftovers are invalid.
-- =============================================================================

CREATE OR REPLACE FUNCTION fn_validate_posting_completeness()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_count INTEGER;
    v_header_id UUID;
    v_posting_index INTEGER;
BEGIN
    -- Coalesce because constraint triggers fire on DELETE too;
    -- NEW is NULL on delete, OLD is NULL on insert.
    v_header_id     := COALESCE(NEW.header_id, OLD.header_id);
    v_posting_index := COALESCE(NEW.posting_index, OLD.posting_index);

    SELECT COUNT(*) INTO v_count
      FROM txn_legs
     WHERE header_id = v_header_id
       AND posting_index = v_posting_index;

    -- 0 legs: posting fully deleted — OK.
    -- 1 leg : incomplete posting — RAISE.
    -- 2 legs: complete posting — OK.
    -- 3+ legs: AFTER trigger fn_validate_posting_cardinality already raised.
    IF v_count = 1 THEN
        RAISE EXCEPTION
            'Posting (header_id=%, posting_index=%) has only 1 leg at '
            'transaction commit; ADR-0019 requires 2.',
            v_header_id, v_posting_index
        USING ERRCODE = 'check_violation';
    END IF;

    RETURN NULL;
END;
$$;

COMMENT ON FUNCTION fn_validate_posting_completeness() IS
    'P3 (migration 065, optimized in 066): DEFERRED constraint trigger '
    'that fires at transaction commit. Per-row indexed lookup verifies '
    'the leg''s own (header_id, posting_index) has 0 or 2 legs at '
    'commit (1 leg = incomplete; 3+ = AFTER trigger fired earlier).';
