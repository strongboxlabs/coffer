-- =============================================================================
-- 058 — split multi-posting MiscInc events into single-posting pairs
-- =============================================================================
--
-- MD's automated Fidelity-statement import path produced 4 "Change in
-- Market Value …" events as multi-posting MiscInc headers (each carries
-- 2 postings that net to zero on both the brokerage and the Adjustment
-- category — bookkeeping wash entries for unrealized-gain tracking).
-- The pattern:
--
--   Posting 0:  brokerage  -X  ↔  Adjustment  +X    (expense flow)
--   Posting 1:  brokerage  +X  ↔  Adjustment  -X    (income flow)
--   Net: $0 on both sides.
--
-- A4's MiscInc editor exposes single-posting events only (matching MD's
-- own MiscInc UI workflow — multi-posting is a side-effect of automated
-- import, not user-creatable). Rather than build complexity into the
-- editor to round-trip these compound events, split them into 2
-- separate MiscInc headers: one for each posting. Net cash effect on
-- the brokerage stays zero (same as today).
--
-- "I hate dropping data" — no data is dropped. Each original event
-- becomes two single-posting events on the same posted_at, with payee
-- suffixed `(expense)` / `(income)` to disambiguate, and external_id
-- suffixed `:e` / `:i` so re-import preserves identity.
-- =============================================================================

DO $$
DECLARE
    v_original RECORD;
    v_new_a UUID;
    v_new_b UUID;
    v_split_count INTEGER := 0;
BEGIN
    -- Defence: any of the targets carry overrides or tags? If so we
    -- shouldn't auto-split — overrides apply at the header level and
    -- would need to be mapped to one or both of the new headers, which
    -- requires user input.
    IF EXISTS (
        SELECT 1
        FROM txn_headers h
        JOIN txn_header_overrides o ON o.header_id = h.id
        WHERE h.action = 'misc_income'
          AND EXISTS (
              SELECT 1 FROM txn_legs l
              WHERE l.header_id = h.id AND l.posting_index > 0
          )
    ) THEN
        RAISE EXCEPTION 'Migration 058: a multi-posting MiscInc header has overrides; split logic doesn''t cover that shape. Inspect manually.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM txn_headers h
        JOIN txn_header_tags t ON t.header_id = h.id
        WHERE h.action = 'misc_income'
          AND EXISTS (
              SELECT 1 FROM txn_legs l
              WHERE l.header_id = h.id AND l.posting_index > 0
          )
    ) THEN
        RAISE EXCEPTION 'Migration 058: a multi-posting MiscInc header has tags; split would need to copy them to both halves.';
    END IF;

    -- Iterate every multi-posting MiscInc header. Pattern-agnostic — if
    -- future imports produce other multi-posting MiscInc shapes, they
    -- get the same treatment.
    FOR v_original IN
        SELECT *
        FROM txn_headers h
        WHERE h.action = 'misc_income'
          AND EXISTS (
              SELECT 1 FROM txn_legs l
              WHERE l.header_id = h.id AND l.posting_index > 0
          )
    LOOP
        v_new_a := gen_random_uuid();
        v_new_b := gen_random_uuid();

        -- Header A — inherits posting 0 (the expense side: brokerage
        -- cash flow is negative).
        INSERT INTO txn_headers (
            id, ledger_id, origin, external_id, payee, memo,
            posted_at, transacted_at, status, check_number,
            is_pending, is_user_defined, is_hidden, is_merged_into,
            import_source,
            online_match_fitid, online_match_fi_id, online_match_status,
            online_match_type, online_match_orig_id,
            cleared_at, cleared_by_user_id, needs_review, action,
            created_at
        )
        VALUES (
            v_new_a, v_original.ledger_id, v_original.origin,
            CASE WHEN v_original.external_id IS NOT NULL
                 THEN v_original.external_id || ':e' ELSE NULL END,
            COALESCE(v_original.payee, '') || ' (expense)',
            v_original.memo,
            v_original.posted_at, v_original.transacted_at, v_original.status,
            v_original.check_number,
            v_original.is_pending, v_original.is_user_defined, v_original.is_hidden,
            v_original.is_merged_into, v_original.import_source,
            v_original.online_match_fitid, v_original.online_match_fi_id,
            v_original.online_match_status, v_original.online_match_type,
            v_original.online_match_orig_id,
            v_original.cleared_at, v_original.cleared_by_user_id,
            v_original.needs_review, v_original.action,
            v_original.created_at
        );

        -- Header B — inherits posting 1 (the income side).
        INSERT INTO txn_headers (
            id, ledger_id, origin, external_id, payee, memo,
            posted_at, transacted_at, status, check_number,
            is_pending, is_user_defined, is_hidden, is_merged_into,
            import_source,
            online_match_fitid, online_match_fi_id, online_match_status,
            online_match_type, online_match_orig_id,
            cleared_at, cleared_by_user_id, needs_review, action,
            created_at
        )
        VALUES (
            v_new_b, v_original.ledger_id, v_original.origin,
            CASE WHEN v_original.external_id IS NOT NULL
                 THEN v_original.external_id || ':i' ELSE NULL END,
            COALESCE(v_original.payee, '') || ' (income)',
            v_original.memo,
            v_original.posted_at, v_original.transacted_at, v_original.status,
            v_original.check_number,
            v_original.is_pending, v_original.is_user_defined, v_original.is_hidden,
            v_original.is_merged_into, v_original.import_source,
            v_original.online_match_fitid, v_original.online_match_fi_id,
            v_original.online_match_status, v_original.online_match_type,
            v_original.online_match_orig_id,
            v_original.cleared_at, v_original.cleared_by_user_id,
            v_original.needs_review, v_original.action,
            v_original.created_at
        );

        -- Reassign legs. Posting 0 → Header A (posting_index stays 0).
        -- Posting 1 → Header B with posting_index reset to 0 (each new
        -- header has exactly one posting).
        UPDATE txn_legs
        SET header_id = v_new_a
        WHERE header_id = v_original.id AND posting_index = 0;

        UPDATE txn_legs
        SET header_id = v_new_b, posting_index = 0
        WHERE header_id = v_original.id AND posting_index = 1;

        -- Original header now has no legs; safe to delete.
        DELETE FROM txn_headers WHERE id = v_original.id;

        v_split_count := v_split_count + 1;
    END LOOP;

    RAISE NOTICE 'Migration 058: split % multi-posting MiscInc events into single-posting pairs.', v_split_count;
END;
$$;


-- -----------------------------------------------------------------------------
-- Verification: no multi-posting MiscInc events remain.
-- -----------------------------------------------------------------------------

DO $$
DECLARE
    v_remaining INTEGER;
BEGIN
    SELECT COUNT(*) INTO v_remaining
    FROM txn_headers h
    WHERE h.action = 'misc_income'
      AND EXISTS (
          SELECT 1 FROM txn_legs l
          WHERE l.header_id = h.id AND l.posting_index > 0
      );

    IF v_remaining > 0 THEN
        RAISE EXCEPTION 'Migration 058: % multi-posting MiscInc events remain after split — investigate.', v_remaining;
    END IF;
END;
$$;
