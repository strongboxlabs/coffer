-- =============================================================================
-- 093 — txn_headers.created_at is immutable (ADR-0034 part 5)
-- =============================================================================
--
-- ADR-0034 elevates (posted_at, created_at, id) to the canonical ordering
-- for every running-window calculation on transactions. The triple is
-- only deterministic if all three columns are immutable after insert.
-- posted_at and id are immutable by existing convention (overrides on
-- posted_at go to txn_header_overrides; id is a PK).
--
-- created_at has no enforcement today. Nothing in the application writes
-- to it after insert, but "no enforcement" + "load-bearing for balance
-- precomputation" is a sharp-edged combination. This migration locks it
-- at the DB level with a column-level UPDATE-rejection trigger — defense
-- in depth, matching the project's posture of enforcing invariants at
-- the layer that owns them (DB owns correctness).
-- =============================================================================

CREATE OR REPLACE FUNCTION fn_reject_txn_headers_created_at_update()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION
            'txn_headers.created_at is immutable (ADR-0034). '
            'header_id=%, old=%, new=%',
            OLD.id, OLD.created_at, NEW.created_at
        USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_reject_txn_headers_created_at_update
BEFORE UPDATE OF created_at ON txn_headers
FOR EACH ROW
EXECUTE FUNCTION fn_reject_txn_headers_created_at_update();

COMMENT ON FUNCTION fn_reject_txn_headers_created_at_update() IS
    'ADR-0034: defends the canonical-ordering invariant by rejecting '
    'any UPDATE that would mutate txn_headers.created_at.';
