-- =============================================================================
-- 101 — Recompute honors txn_leg_overrides.amount (last ADR-0034 caveat)
-- =============================================================================
--
-- Closes the LAST remaining caveat from ADR-0004 §4 / ADR-0034:
-- `resolved_transactions.amount` is COALESCE(lo.amount, l.amount), but
-- the balance trigger summed l.amount directly. Editing a leg's
-- amount via txn_leg_overrides changed what the user saw on the
-- register row WITHOUT shifting the running balance — divergence
-- between the displayed amount and the displayed balance_after.
--
-- Two changes, mirroring mig 099's posted_at fix:
--   1. fn_recompute_balances_for_account uses
--      COALESCE(lo.amount, l.amount) in the header_net SUM.
--   2. New statement-level triggers on txn_leg_overrides for
--      INSERT / UPDATE-of-amount / DELETE; each recomputes the
--      affected leg's account anchored at the header's effective
--      posted_at (already override-aware after mig 099).
--
-- After this migration, every override layer (posted_at, amount,
-- payee/memo/etc.) either participates in or is intentionally
-- excluded from balance computation. The trigger family covers all
-- mutations: leg INSERT/UPDATE/DELETE, header UPDATE, header-override
-- INSERT/UPDATE/DELETE, and now leg-override INSERT/UPDATE/DELETE.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1) Recompute function: SUM uses COALESCE(lo.amount, l.amount) so the
--    override's amount drives the balance walk wherever it's set.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_recompute_balances_for_account(
    p_account_id     UUID,
    p_from_posted_at TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
    v_starting  NUMERIC(19, 4);
    v_ledger_id UUID;
BEGIN
    SELECT a.ledger_id INTO v_ledger_id FROM accounts a WHERE a.id = p_account_id;
    IF v_ledger_id IS NULL THEN
        RETURN;
    END IF;

    SELECT thab.balance_after
      INTO v_starting
      FROM txn_header_account_balances thab
      JOIN txn_headers h ON h.id = thab.header_id
      LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     WHERE thab.account_id = p_account_id
       AND COALESCE(o.posted_at, h.posted_at) < p_from_posted_at
     ORDER BY COALESCE(o.posted_at, h.posted_at) DESC, h.seq DESC
     LIMIT 1;

    IF v_starting IS NULL THEN
        SELECT a.opening_balance INTO v_starting FROM accounts a WHERE a.id = p_account_id;
    END IF;
    v_starting := COALESCE(v_starting, 0);

    DELETE FROM txn_header_account_balances thab
     USING txn_headers h
      LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     WHERE thab.header_id = h.id
       AND thab.account_id = p_account_id
       AND COALESCE(o.posted_at, h.posted_at) >= p_from_posted_at;

    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after, net_amount)
    WITH header_net AS (
        SELECT h.id AS header_id,
               COALESCE(o.posted_at, h.posted_at) AS posted_at,
               h.seq,
               -- Mig 101: per-leg amount honours txn_leg_overrides.amount.
               -- The recompute walks effective amounts the same way the
               -- view does (COALESCE(lo.amount, l.amount)), so the
               -- balance_after column always agrees with the amount
               -- column the user sees on the register row.
               SUM(COALESCE(lo.amount, l.amount)) AS net_amount
          FROM txn_headers h
          JOIN txn_legs l ON l.header_id = h.id
          LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
          LEFT JOIN txn_header_overrides o ON o.header_id = h.id
         WHERE l.account_id = p_account_id
           AND h.is_merged_into IS NULL
           AND COALESCE(o.posted_at, h.posted_at) >= p_from_posted_at
         GROUP BY h.id, COALESCE(o.posted_at, h.posted_at), h.seq
    )
    SELECT
        header_id,
        p_account_id,
        v_ledger_id,
        v_starting + SUM(net_amount) OVER (
            ORDER BY posted_at, seq
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS balance_after,
        net_amount
      FROM header_net;
END;
$$ LANGUAGE plpgsql;

-- -----------------------------------------------------------------------------
-- 2) Statement-level triggers on txn_leg_overrides.
-- -----------------------------------------------------------------------------

-- INSERT: new override row — leg's effective amount changes from
-- l.amount to new.amount. Recompute the leg's account anchored at the
-- header's effective posted_at.
CREATE OR REPLACE FUNCTION fn_trg_leg_overrides_insert_recompute()
RETURNS TRIGGER AS $$
DECLARE
    rec RECORD;
BEGIN
    IF pg_trigger_depth() > 1 THEN
        RETURN NULL;
    END IF;

    FOR rec IN
        SELECT l.account_id, MIN(COALESCE(o.posted_at, h.posted_at)) AS dt
          FROM new_rows n
          JOIN txn_legs l ON l.id = n.leg_id
          JOIN txn_headers h ON h.id = l.header_id
          LEFT JOIN txn_header_overrides o ON o.header_id = h.id
         WHERE n.amount IS NOT NULL
         GROUP BY l.account_id
    LOOP
        PERFORM fn_recompute_balances_for_account(rec.account_id, rec.dt);
    END LOOP;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- UPDATE: only when amount actually changed (column-level trigger
-- clause filters at the SQL level; this is a defense-in-depth check).
CREATE OR REPLACE FUNCTION fn_trg_leg_overrides_update_recompute()
RETURNS TRIGGER AS $$
DECLARE
    rec RECORD;
BEGIN
    IF pg_trigger_depth() > 1 THEN
        RETURN NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
          FROM new_rows n
          JOIN old_rows o ON o.leg_id = n.leg_id
         WHERE n.amount IS DISTINCT FROM o.amount
    ) THEN
        RETURN NULL;
    END IF;

    FOR rec IN
        SELECT l.account_id, MIN(COALESCE(ho.posted_at, h.posted_at)) AS dt
          FROM new_rows n
          JOIN txn_legs l ON l.id = n.leg_id
          JOIN txn_headers h ON h.id = l.header_id
          LEFT JOIN txn_header_overrides ho ON ho.header_id = h.id
         GROUP BY l.account_id
    LOOP
        PERFORM fn_recompute_balances_for_account(rec.account_id, rec.dt);
    END LOOP;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- DELETE: leg's effective amount reverts to l.amount; same recompute
-- shape as INSERT.
CREATE OR REPLACE FUNCTION fn_trg_leg_overrides_delete_recompute()
RETURNS TRIGGER AS $$
DECLARE
    rec RECORD;
BEGIN
    IF pg_trigger_depth() > 1 THEN
        RETURN NULL;
    END IF;

    FOR rec IN
        SELECT l.account_id, MIN(COALESCE(ho.posted_at, h.posted_at)) AS dt
          FROM old_rows o
          JOIN txn_legs l ON l.id = o.leg_id
          JOIN txn_headers h ON h.id = l.header_id
          LEFT JOIN txn_header_overrides ho ON ho.header_id = h.id
         WHERE o.amount IS NOT NULL
         GROUP BY l.account_id
    LOOP
        PERFORM fn_recompute_balances_for_account(rec.account_id, rec.dt);
    END LOOP;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_leg_overrides_recompute_insert
AFTER INSERT ON txn_leg_overrides
REFERENCING NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_leg_overrides_insert_recompute();

CREATE TRIGGER trg_leg_overrides_recompute_update
AFTER UPDATE ON txn_leg_overrides
REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_leg_overrides_update_recompute();

CREATE TRIGGER trg_leg_overrides_recompute_delete
AFTER DELETE ON txn_leg_overrides
REFERENCING OLD TABLE AS old_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_leg_overrides_delete_recompute();

-- -----------------------------------------------------------------------------
-- 3) One-shot recompute. The recompute function now honours
--    leg-amount overrides; any account with at least one
--    txn_leg_overrides.amount row has a stale balance. Walk every
--    account and re-derive.
-- -----------------------------------------------------------------------------
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
