-- =============================================================================
-- 059 — enforce single-posting MiscInc as a structural invariant
-- =============================================================================
--
-- Migration 058 split the 4 multi-posting MiscInc events that existed
-- in real data (MD's automated Fidelity-import path bundles multiple
-- inc/fee splits into one txn). The importer (this PR) is rewired to
-- fan out at import time so future re-imports produce single-posting
-- MiscInc headers natively.
--
-- This trigger closes the loop: any future write that attempts to
-- create a multi-posting MiscInc header (via the API, a manual SQL
-- run, or a regressed importer) fails fast.
--
--   Invariant:  action = 'misc_income'  ⇒  exactly one distinct
--                                          posting_index across legs
--
-- Implementation: AFTER INSERT OR UPDATE on txn_legs; for any leg
-- whose header is misc_income, re-count distinct posting_index on
-- the header and raise if > 1.
--
-- Why AFTER (not BEFORE): BEFORE row-level triggers can't see the
-- post-insert state of sibling rows in the same INSERT statement.
-- AFTER triggers fire per-row, post-write, with full visibility of
-- the now-current state. Bulk inserts of a multi-posting MiscInc
-- header fail loudly on the second leg's trigger pass.
-- =============================================================================

CREATE OR REPLACE FUNCTION fn_validate_miscincome_single_posting()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_action TEXT;
    v_postings INTEGER;
BEGIN
    SELECT action INTO v_action FROM txn_headers WHERE id = NEW.header_id;
    IF v_action IS DISTINCT FROM 'misc_income' THEN
        RETURN NEW;
    END IF;

    SELECT COUNT(DISTINCT posting_index) INTO v_postings
    FROM txn_legs WHERE header_id = NEW.header_id;

    IF v_postings > 1 THEN
        RAISE EXCEPTION
            'misc_income headers must have exactly one posting '
            '(header_id=%, postings=%). MD''s compound MiscInc shapes '
            'are fanned out at import time — see InvestmentTransactionMapper.BuildHeaderPerPair.',
            NEW.header_id, v_postings;
    END IF;

    RETURN NEW;
END;
$$;

COMMENT ON FUNCTION fn_validate_miscincome_single_posting() IS
    'Enforces the single-posting MiscInc invariant established by '
    'migration 058 + the corresponding importer fan-out (Path B). '
    'Real MD MiscInc data only ever bundles multiple postings via '
    'automated-import paths (Fidelity statements, etc.); user-created '
    'MiscInc events in MD''s UI are single-posting.';

CREATE TRIGGER trg_validate_miscincome_single_posting
AFTER INSERT OR UPDATE ON txn_legs
FOR EACH ROW
EXECUTE FUNCTION fn_validate_miscincome_single_posting();
