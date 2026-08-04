-- =============================================================================
-- 074 — Convert fn_validate_posting_cardinality to statement-level
-- =============================================================================
--
-- BEFORE
--
-- `trg_validate_posting_cardinality` is AFTER INSERT OR UPDATE FOR
-- EACH ROW on txn_legs. For every leg INSERT it runs:
--     SELECT COUNT(*) FROM txn_legs
--      WHERE header_id = NEW.header_id AND posting_index = NEW.posting_index
-- For a 4-leg save: 4 fires × 1 COUNT query each = 4 queries. For an
-- importer batch inserting 100k legs: 100k queries. Scales poorly.
--
-- AFTER
--
-- One statement-level fire walks the transition table once, finds
-- distinct (header_id, posting_index) pairs, runs a single grouped
-- COUNT that flags any pair with >2 legs. Same correctness, O(N)
-- queries → O(1) regardless of batch size.
--
-- The invariant remains: every posting has exactly 2 legs (ADR-0019).
-- 0 legs is allowed mid-statement (deferred-completeness trigger
-- catches "1 leg at commit"); 3+ is always a bug and we still raise
-- immediately.
-- =============================================================================

CREATE OR REPLACE FUNCTION fn_validate_posting_cardinality_stmt()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_bad RECORD;
BEGIN
    -- Find any (header, posting_index) in the transition table that
    -- now has >2 legs. Use a CTE to reduce the join to a single
    -- aggregate pass.
    SELECT
        l.header_id,
        l.posting_index,
        COUNT(*) AS leg_count
      INTO v_bad
      FROM txn_legs l
     WHERE (l.header_id, l.posting_index) IN (
         SELECT DISTINCT header_id, posting_index FROM affected_legs
     )
     GROUP BY l.header_id, l.posting_index
    HAVING COUNT(*) > 2
     LIMIT 1;

    IF FOUND THEN
        RAISE EXCEPTION
            'Posting (header_id=%, posting_index=%) has % legs; the '
            'symmetric-postings invariant (ADR-0019) requires exactly 2.',
            v_bad.header_id, v_bad.posting_index, v_bad.leg_count
        USING ERRCODE = 'check_violation';
    END IF;

    RETURN NULL;
END;
$$;

-- Drop the per-row trigger; add statement-level INSERT and UPDATE
-- triggers, each with its own transition-table alias to the same
-- function. (Both pass the affected rows via a `affected_legs`
-- alias for uniform body.)
DROP TRIGGER IF EXISTS trg_validate_posting_cardinality ON txn_legs;

DROP TRIGGER IF EXISTS trg_validate_posting_cardinality_insert ON txn_legs;
CREATE TRIGGER trg_validate_posting_cardinality_insert
    AFTER INSERT ON txn_legs
    REFERENCING NEW TABLE AS affected_legs
    FOR EACH STATEMENT
    EXECUTE FUNCTION fn_validate_posting_cardinality_stmt();

DROP TRIGGER IF EXISTS trg_validate_posting_cardinality_update ON txn_legs;
CREATE TRIGGER trg_validate_posting_cardinality_update
    AFTER UPDATE ON txn_legs
    REFERENCING NEW TABLE AS affected_legs
    FOR EACH STATEMENT
    EXECUTE FUNCTION fn_validate_posting_cardinality_stmt();

COMMENT ON FUNCTION fn_validate_posting_cardinality_stmt() IS
    'Statement-level cardinality check (074). Reads the affected_legs '
    'transition table, finds distinct (header_id, posting_index) pairs '
    'touched by this statement, and raises if any pair now exceeds 2 '
    'legs (ADR-0019). One grouped query per statement regardless of '
    'batch size; replaces the per-row variant from earlier migrations.';
