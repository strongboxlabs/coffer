-- =============================================================================
-- 102 — Drop balance triggers; recompute moves to API call sites
-- =============================================================================
--
-- WHY (continued from ADR-0032 "triggers as last resort")
--
-- The balance-trigger family has proven a continuous source of bugs:
--   * mig 026: leg-DELETE cascade-from-header race
--   * mig 094: same race re-introduced under the header-walk family
--   * mig 099: posted_at overrides were silently bypassed
--   * mig 101: leg-amount overrides were silently bypassed
--   * Latent today: EF batched SaveChanges with a merge + postings
--     reshape produces stale balance rows pointing to wrong accounts
--     (Uncategorized leftover when the counter leg's account_id was
--     UPDATEd in the same batch as a header is_merged_into UPDATE).
--
-- Each fix added more trigger surface. Each new surface introduced
-- new edge cases. The structural answer is to stop hiding the
-- recompute behind triggers entirely — make it explicit at every
-- writer.
--
-- WHAT CHANGES
--
-- Every trigger that called fn_recompute_balances_for_account is
-- dropped, along with its handler function. The recompute function
-- itself stays — it's still the algorithm. It's invoked explicitly
-- from the API and importer code paths instead of fired by triggers.
--
-- For EF Core to invoke the recompute via LINQ (no raw SQL per the
-- project's data-access policy), the function is wrapped in a TVF
-- with a typed return shape, bound via HasDbFunction in AppDbContext.
--
-- DROPPED
--
--   * fn_trg_legs_recompute_balances() + 3 triggers on txn_legs
--   * fn_trg_headers_recompute_balances() + 1 trigger on txn_headers
--   * fn_trg_header_overrides_insert_recompute()
--   * fn_trg_header_overrides_update_recompute()
--   * fn_trg_header_overrides_delete_recompute()
--     + 3 triggers on txn_header_overrides
--   * fn_trg_leg_overrides_insert_recompute()
--   * fn_trg_leg_overrides_update_recompute()
--   * fn_trg_leg_overrides_delete_recompute()
--     + 3 triggers on txn_leg_overrides
--
-- KEPT
--
--   * fn_recompute_balances_for_account(UUID, TIMESTAMPTZ) — the
--     algorithm; invoked explicitly from C# now.
--   * trg_reject_txn_headers_created_at_update (mig 093)
--   * trg_reject_txn_headers_seq_update (mig 095)
--     Both are invariant-lockdown triggers, not recompute triggers.
--     They reject mutations to immutable columns; no algorithm
--     hidden in them.
--
-- ADDED
--
--   * recompute_balances_for_account(UUID, TIMESTAMPTZ) — TVF wrapper
--     returning (account_id UUID) so EF Core's HasDbFunction can
--     bind it. Calls fn_recompute_balances_for_account under the
--     hood; returns the account_id passed in for the LINQ projection.
--
-- ONE-SHOT REPAIR
--
-- Existing data has 38+ stale balance rows from the trigger-batching
-- bug. The final DO block recomputes every account, restoring
-- consistency before the API takes over the recompute responsibility.
-- =============================================================================

-- 1) Drop triggers.

DROP TRIGGER IF EXISTS trg_legs_recompute_balances_insert ON txn_legs;
DROP TRIGGER IF EXISTS trg_legs_recompute_balances_update ON txn_legs;
DROP TRIGGER IF EXISTS trg_legs_recompute_balances_delete ON txn_legs;
DROP TRIGGER IF EXISTS trg_headers_recompute_balances_update ON txn_headers;
DROP TRIGGER IF EXISTS trg_header_overrides_recompute_insert ON txn_header_overrides;
DROP TRIGGER IF EXISTS trg_header_overrides_recompute_update ON txn_header_overrides;
DROP TRIGGER IF EXISTS trg_header_overrides_recompute_delete ON txn_header_overrides;
DROP TRIGGER IF EXISTS trg_leg_overrides_recompute_insert ON txn_leg_overrides;
DROP TRIGGER IF EXISTS trg_leg_overrides_recompute_update ON txn_leg_overrides;
DROP TRIGGER IF EXISTS trg_leg_overrides_recompute_delete ON txn_leg_overrides;

-- 2) Drop trigger handler functions. fn_recompute_balances_for_account
-- (the algorithm) is NOT dropped; it stays callable.

DROP FUNCTION IF EXISTS fn_trg_legs_recompute_balances();
DROP FUNCTION IF EXISTS fn_trg_headers_recompute_balances();
DROP FUNCTION IF EXISTS fn_trg_header_overrides_insert_recompute();
DROP FUNCTION IF EXISTS fn_trg_header_overrides_update_recompute();
DROP FUNCTION IF EXISTS fn_trg_header_overrides_delete_recompute();
DROP FUNCTION IF EXISTS fn_trg_leg_overrides_insert_recompute();
DROP FUNCTION IF EXISTS fn_trg_leg_overrides_update_recompute();
DROP FUNCTION IF EXISTS fn_trg_leg_overrides_delete_recompute();

-- 3) TVF wrapper for HasDbFunction binding. EF Core's
-- HasDbFunction works cleanly with TVFs returning a typed shape;
-- void scalar functions are awkward. Returning the input account_id
-- gives the LINQ side something to project, even though the caller
-- discards it.

CREATE OR REPLACE FUNCTION recompute_balances_for_account(
    p_account_id     UUID,
    p_from_posted_at TIMESTAMPTZ
) RETURNS TABLE(account_id UUID)
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM fn_recompute_balances_for_account(p_account_id, p_from_posted_at);
    RETURN QUERY SELECT p_account_id;
END;
$$;

COMMENT ON FUNCTION recompute_balances_for_account(UUID, TIMESTAMPTZ) IS
    'TVF wrapper over fn_recompute_balances_for_account so EF Core can '
    'invoke the recompute via HasDbFunction-bound LINQ. Caller discards '
    'the returned account_id; the side effect on txn_header_account_balances '
    'is what matters. ADR-0034 / ADR-0032 (recompute at call sites, no '
    'triggers).';

GRANT EXECUTE ON FUNCTION recompute_balances_for_account(UUID, TIMESTAMPTZ)
    TO coffer_app;

-- 4) One-shot repair of any (header, account) rows orphaned by the
-- trigger-batching bug. Walks every account with legs; re-derives
-- every balance row from canonical (posted_at, seq) order using
-- the live legs' COALESCE(lo.amount, l.amount) and effective
-- COALESCE(o.posted_at, h.posted_at). Same body as the prior
-- backfills; this is the LAST trigger-implicit recompute the schema
-- will see — after this migration the responsibility passes to the
-- API and importer.

DO $$
DECLARE
    v_account_id UUID;
BEGIN
    FOR v_account_id IN
        SELECT DISTINCT account_id FROM txn_legs
    LOOP
        PERFORM fn_recompute_balances_for_account(
            v_account_id,
            '0001-01-01'::TIMESTAMPTZ
        );
    END LOOP;
END $$;
