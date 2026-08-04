-- =============================================================================
-- 061 — Reverse the misc_income single-posting split (058) and drop the
--       single-posting invariant trigger (059).
-- =============================================================================
--
-- Background
-- ----------
-- Migrations 058 + 059 and the importer's Path B fan-out were predicated
-- on the belief that the 4 "Change in Market Value …" multi-posting
-- MiscInc events in the dev DB were a Moneydance automated-import
-- artifact (Fidelity-statement import path), and that user-creatable
-- MiscInc was always single-posting.
--
-- Closer inspection of MD's raw JSON data (see docs/moneydance-investment-actions.md)
-- showed the opposite: the 4 events are the standard MD `inc` txn shape
-- with the OPTIONAL `fee` split present — i.e. `[sec, fee, inc]`,
-- exactly what a user would emit from MD's UI by entering a MiscInc
-- transaction with a fee field filled in. The events being
-- net-zero on both legs was the user's bookkeeping pattern (same
-- Adjustment category on both sides), NOT a structural anomaly.
--
-- Conclusion: multi-posting MiscInc is a legitimate user-creatable
-- MD shape. The "single-posting invariant" we built around 058/059
-- forced unnecessary fan-out and made the Ledger model less
-- expressive than MD's.
--
-- This migration:
--   1. Drops the trigger and function from 059 (the invariant goes away).
--   2. Reconstitutes each split pair into one multi-posting header
--      (058's data change reversed in-place by reusing the `:e`
--      header and absorbing the `:i` legs as posting_index = 1).
--
-- The importer Path B fan-out is removed in the companion C# change
-- shipped in the same PR.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Part 1: drop the 059 trigger and function.
-- -----------------------------------------------------------------------------

DROP TRIGGER IF EXISTS trg_validate_miscincome_single_posting ON txn_legs;
DROP FUNCTION IF EXISTS fn_validate_miscincome_single_posting();


-- -----------------------------------------------------------------------------
-- Part 2: reconstitute the 4 multi-posting MiscInc events.
--
-- Reuse the `:e` header as the surviving multi-posting header (strip its
-- suffixes), absorb the `:i` header's legs as posting_index = 1, then
-- delete the now-empty `:i` header. Mirror-image of migration 058's
-- split logic.
-- -----------------------------------------------------------------------------

DO $$
DECLARE
    v_e RECORD;
    v_i_id UUID;
    v_base_external_id TEXT;
    v_base_payee TEXT;
    v_reconstituted INTEGER := 0;
BEGIN
    -- Defence: if any `:e` or `:i` header carries overrides or tags, the
    -- merge would need to reconcile them. Bail rather than guess.
    IF EXISTS (
        SELECT 1
        FROM txn_headers h
        JOIN txn_header_overrides o ON o.header_id = h.id
        WHERE h.action = 'misc_income'
          AND (h.external_id LIKE '%:e' OR h.external_id LIKE '%:i')
    ) THEN
        RAISE EXCEPTION 'Migration 061: a split MiscInc header has overrides; reconstitution logic doesn''t cover that shape. Inspect manually.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM txn_headers h
        JOIN txn_header_tags t ON t.header_id = h.id
        WHERE h.action = 'misc_income'
          AND (h.external_id LIKE '%:e' OR h.external_id LIKE '%:i')
    ) THEN
        RAISE EXCEPTION 'Migration 061: a split MiscInc header has tags; reconstitution would need to copy them.';
    END IF;

    FOR v_e IN
        SELECT *
        FROM txn_headers
        WHERE action = 'misc_income'
          AND external_id LIKE '%:e'
    LOOP
        v_base_external_id := substring(v_e.external_id from 1 for length(v_e.external_id) - 2);

        SELECT id INTO v_i_id
        FROM txn_headers
        WHERE action = 'misc_income'
          AND external_id = v_base_external_id || ':i';

        IF v_i_id IS NULL THEN
            RAISE EXCEPTION 'Migration 061: no :i companion found for header % (external_id=%)',
                v_e.id, v_e.external_id;
        END IF;

        v_base_payee := regexp_replace(COALESCE(v_e.payee, ''), ' \(expense\)$', '');

        -- Strip the :e suffix from external_id and the (expense) suffix
        -- from payee on the surviving header.
        UPDATE txn_headers
        SET external_id = v_base_external_id,
            payee = v_base_payee
        WHERE id = v_e.id;

        -- Absorb the :i header's legs as posting_index = 1.
        UPDATE txn_legs
        SET header_id = v_e.id,
            posting_index = 1
        WHERE header_id = v_i_id;

        DELETE FROM txn_headers WHERE id = v_i_id;

        v_reconstituted := v_reconstituted + 1;
    END LOOP;

    RAISE NOTICE 'Migration 061: reconstituted % multi-posting MiscInc event(s) from split halves.',
        v_reconstituted;
END;
$$;


-- -----------------------------------------------------------------------------
-- Verification: no `:e` or `:i` external_id suffixes remain on misc_income
-- headers; the reconstituted events each have exactly 2 postings.
-- -----------------------------------------------------------------------------

DO $$
DECLARE
    v_remaining_suffix INTEGER;
    v_multi_posting INTEGER;
BEGIN
    SELECT COUNT(*) INTO v_remaining_suffix
    FROM txn_headers
    WHERE action = 'misc_income'
      AND (external_id LIKE '%:e' OR external_id LIKE '%:i');

    IF v_remaining_suffix > 0 THEN
        RAISE EXCEPTION 'Migration 061: % misc_income header(s) still carry :e/:i suffix after reconstitution.', v_remaining_suffix;
    END IF;

    SELECT COUNT(*) INTO v_multi_posting
    FROM (
        SELECT h.id
        FROM txn_headers h
        JOIN txn_legs l ON l.header_id = h.id
        WHERE h.action = 'misc_income'
        GROUP BY h.id
        HAVING COUNT(DISTINCT l.posting_index) > 1
    ) s;

    RAISE NOTICE 'Migration 061: % misc_income header(s) now carry > 1 posting (the reconstituted events).', v_multi_posting;
END;
$$;
