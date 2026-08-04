-- 026_fix_legs_delete_trigger_after_header_cascade.sql
--
-- Trigger ordering bug surfaced by verify_balance_trigger.sql under
-- the ADR-0022 schema: when `DELETE FROM txn_headers WHERE id = $X`
-- fires, Postgres cascades to delete the associated `txn_legs` rows
-- and then runs the AFTER DELETE STATEMENT trigger on `txn_legs`
-- (`fn_trg_legs_balance_after`). By that time the parent header is
-- already gone, so the trigger function's join to `txn_headers`
-- (to find the affected accounts' earliest posted_at) finds no
-- matching row — the recompute is skipped and balance_after on the
-- account stays stale.
--
-- Fix: switch the join in the DELETE branch to LEFT JOIN with a
-- COALESCE fallback to an early-enough date. When the header is
-- gone the recompute walks the entire account from opening_balance,
-- which is correct (just more work than the targeted recompute).
-- Cascade-from-header-delete is rare in normal use; the safety
-- matters more than the targeted optimisation.
--
-- Migration 023's original function is replaced via CREATE OR
-- REPLACE; no schema change.

CREATE OR REPLACE FUNCTION fn_trg_legs_balance_after()
RETURNS TRIGGER AS $$
DECLARE
    rec RECORD;
BEGIN
    IF pg_trigger_depth() > 1 THEN
        RETURN NULL;
    END IF;

    IF TG_OP = 'INSERT' THEN
        FOR rec IN
            SELECT n.account_id, MIN(h.posted_at) AS dt
              FROM new_rows n
              JOIN txn_headers h ON h.id = n.header_id
             GROUP BY n.account_id
        LOOP
            PERFORM fn_recompute_legs_balance_after(rec.account_id, rec.dt);
        END LOOP;
    ELSIF TG_OP = 'DELETE' THEN
        -- LEFT JOIN + COALESCE: when the parent header was already
        -- removed (cascade from txn_headers DELETE), the join returns
        -- NULL and we fall back to recomputing the whole account from
        -- opening_balance.
        FOR rec IN
            SELECT o.account_id,
                   COALESCE(MIN(h.posted_at), '1900-01-01'::timestamptz) AS dt
              FROM old_rows o
              LEFT JOIN txn_headers h ON h.id = o.header_id
             GROUP BY o.account_id
        LOOP
            PERFORM fn_recompute_legs_balance_after(rec.account_id, rec.dt);
        END LOOP;
    ELSE  -- UPDATE
        IF NOT EXISTS (
            SELECT 1
              FROM new_rows n
              JOIN old_rows o ON o.id = n.id
             WHERE n.amount     IS DISTINCT FROM o.amount
                OR n.account_id IS DISTINCT FROM o.account_id
        ) THEN
            RETURN NULL;
        END IF;

        FOR rec IN
            SELECT account_id, MIN(posted_at) AS dt
              FROM (
                  SELECT n.account_id, h.posted_at
                    FROM new_rows n JOIN txn_headers h ON h.id = n.header_id
                  UNION ALL
                  SELECT o.account_id, h.posted_at
                    FROM old_rows o JOIN txn_headers h ON h.id = o.header_id
              ) merged
             GROUP BY account_id
        LOOP
            PERFORM fn_recompute_legs_balance_after(rec.account_id, rec.dt);
        END LOOP;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;
