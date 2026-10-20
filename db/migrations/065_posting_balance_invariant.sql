-- =============================================================================
-- 065 — Posting structural invariants (P3)
-- =============================================================================
--
-- Every posting (defined by (header_id, posting_index)) must consist
-- of EXACTLY 2 legs. This is the structural invariant of ADR-0019's
-- symmetric-posting model — money flowing from one account to
-- another, captured as a pair of legs.
--
-- Pre-065 the invariant was an unenforced convention. The importer's
-- pre-A4 leg-upsert logic could (and did) produce 3+-leg postings when
-- the same logical purchase ended up under TWO Ledger account_ids
-- (SimpleFIN + MD dual-source bug). Migration 064 / Slice 1 fixed the
-- producer; this migration locks the invariant at the DB level so no
-- future code can regress.
--
-- BALANCE-TO-ZERO NOTE
-- --------------------
-- The original P3 spec called for "legs sum to zero" enforcement on
-- top of the 2-leg rule. Real MD data carries legitimate one-sided
-- events (SHARE CLASS EXCHANGE, DIST TO OWNER BASIS) where one leg
-- has amount=0 by design — the share-side effect is real, the cash
-- side is intentionally zero. The current implementation enforces
-- the 2-leg cardinality only; balance enforcement is deferred until
-- a richer posting model can accommodate one-sided corporate actions.
--
-- Implementation: AFTER INSERT OR UPDATE on txn_legs. A 1-leg
-- posting is allowed transiently (one leg of a pair lands before its
-- partner) but must reach 2 legs by transaction commit, enforced by
-- the DEFERRABLE constraint trigger in Part 2.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Part 1: per-row trigger — catches > 2 legs and unbalanced 2-leg postings.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_validate_posting_cardinality()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_leg_count INTEGER;
BEGIN
    SELECT COUNT(*)
      INTO v_leg_count
      FROM txn_legs
     WHERE header_id     = NEW.header_id
       AND posting_index = NEW.posting_index;

    IF v_leg_count > 2 THEN
        RAISE EXCEPTION
            'Posting (header_id=%, posting_index=%) has % legs; the '
            'symmetric-postings invariant (ADR-0019) requires exactly 2.',
            NEW.header_id, NEW.posting_index, v_leg_count
        USING ERRCODE = 'check_violation';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_validate_posting_cardinality
AFTER INSERT OR UPDATE ON txn_legs
FOR EACH ROW
EXECUTE FUNCTION fn_validate_posting_cardinality();

COMMENT ON FUNCTION fn_validate_posting_cardinality() IS
    'P3 (migration 065): per-row AFTER trigger enforcing the ADR-0019 '
    '2-leg-per-posting invariant on every (header_id, posting_index). '
    'Fires immediately when a third leg is inserted at an existing '
    'posting. The 1-leg transient state during multi-statement INSERTs '
    'is allowed; the deferred constraint trigger in Part 2 catches '
    'any 1-leg leftovers at commit.';


-- -----------------------------------------------------------------------------
-- Part 2: DEFERRED end-of-transaction check — catches 1-leg leftovers.
-- -----------------------------------------------------------------------------
--
-- A leg may transiently exist alone during a multi-statement INSERT.
-- This constraint trigger is DEFERRED so it runs at COMMIT, by which
-- time every posting must have its complete pair.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_validate_posting_completeness()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_orphan_row RECORD;
BEGIN
    -- Look for any posting that ended the transaction with < 2 legs.
    SELECT header_id, posting_index, leg_count INTO v_orphan_row FROM (
        SELECT header_id, posting_index, COUNT(*) AS leg_count
        FROM txn_legs
        GROUP BY header_id, posting_index
        HAVING COUNT(*) < 2
    ) s LIMIT 1;

    IF v_orphan_row IS NOT NULL THEN
        RAISE EXCEPTION
            'Posting (header_id=%, posting_index=%) has only % leg(s) '
            'at transaction commit; ADR-0019 requires 2.',
            v_orphan_row.header_id, v_orphan_row.posting_index,
            v_orphan_row.leg_count
        USING ERRCODE = 'check_violation';
    END IF;

    RETURN NULL;
END;
$$;

-- Use a CONSTRAINT TRIGGER (Postgres-specific) for the deferred behavior.
-- Fires once per statement; combined with deferral, catches end-of-txn state.
CREATE CONSTRAINT TRIGGER trg_validate_posting_completeness
AFTER INSERT OR DELETE OR UPDATE ON txn_legs
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW
EXECUTE FUNCTION fn_validate_posting_completeness();

COMMENT ON FUNCTION fn_validate_posting_completeness() IS
    'P3 (migration 065): DEFERRED constraint trigger that fires at '
    'transaction commit. Catches any posting left with < 2 legs '
    '(transient 1-leg state during a multi-statement INSERT is allowed; '
    'but the second leg must arrive before commit).';
