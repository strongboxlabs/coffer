-- =============================================================================
-- 094 — Fix legs-recompute DELETE branch for header-cascade ordering
-- =============================================================================
--
-- Same trigger-ordering bug that motivated migration 026 against the
-- leg-walk trigger family: when `DELETE FROM txn_headers WHERE id = $X`
-- cascades to delete child legs, the AFTER DELETE STATEMENT trigger on
-- txn_legs fires AFTER both the legs and the header are gone. The
-- function's INNER JOIN from old_rows.header_id to txn_headers.id finds
-- no matching row → MIN(h.posted_at) is NULL → the recompute loop has
-- no rows to iterate over → the account's running balance stays stale.
--
-- Mig 090 reintroduced this race because the new trigger function uses
-- the same INNER JOIN pattern. Fix: LEFT JOIN + COALESCE fallback in
-- the DELETE branch, identical to the mig 026 remediation. When the
-- header is gone, we recompute the entire account from opening_balance
-- — more work than the targeted recompute, but correct, and cascade-
-- from-header-delete is rare in normal use.
-- =============================================================================

CREATE OR REPLACE FUNCTION fn_trg_legs_recompute_balances()
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
            PERFORM fn_recompute_balances_for_account(rec.account_id, rec.dt);
        END LOOP;
    ELSIF TG_OP = 'DELETE' THEN
        -- LEFT JOIN + COALESCE so cascade-from-header-DELETE still
        -- recomputes the affected account (mig 026 fix, reapplied to
        -- the header-walk family).
        FOR rec IN
            SELECT o.account_id,
                   COALESCE(MIN(h.posted_at), '1900-01-01'::timestamptz) AS dt
              FROM old_rows o
              LEFT JOIN txn_headers h ON h.id = o.header_id
             GROUP BY o.account_id
        LOOP
            PERFORM fn_recompute_balances_for_account(rec.account_id, rec.dt);
        END LOOP;
    ELSE  -- UPDATE
        IF NOT EXISTS (
            SELECT 1
              FROM new_rows n
              JOIN old_rows o ON o.id = n.id
             WHERE n.amount     IS DISTINCT FROM o.amount
                OR n.account_id IS DISTINCT FROM o.account_id
                OR n.header_id  IS DISTINCT FROM o.header_id
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
            PERFORM fn_recompute_balances_for_account(rec.account_id, rec.dt);
        END LOOP;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;
