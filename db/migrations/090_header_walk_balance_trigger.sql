-- =============================================================================
-- 090 — Header-walk running-balance trigger family (ADR-0034 part 2)
-- =============================================================================
--
-- Replaces the leg-walk trigger family installed in mig 023 with a
-- header-walk family that writes to txn_header_account_balances (mig 089).
--
-- WHY
--
-- The old function fn_recompute_legs_balance_after walked legs ordered
-- by (h.posted_at, l.id). When a header had multiple cash legs on the
-- same account (Slice A4 BuyXfr fan-out, future bank splits), running
-- balance jumped between intermediate leg-walk values that didn't
-- represent any real cash state. Random-UUID tiebreaker on same-
-- timestamp legs made recompute non-deterministic relative to the
-- write path. See ADR-0034 for full rationale.
--
-- WHAT CHANGES
--
-- New recompute function aggregates legs per header first, then runs
-- a window-SUM in canonical (posted_at, created_at, id) order. Writes
-- into txn_header_account_balances. The (header, account) pair is the
-- unit of accounting; multi-leg same-account headers collapse to one
-- step in the running total.
--
-- One-shot backfill at the end of this migration populates the new
-- table for every account in every ledger.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1) The recompute primitive.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_recompute_balances_for_account(
    p_account_id     UUID,
    p_from_posted_at TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
    v_starting  NUMERIC(19, 4);
    v_ledger_id UUID;
BEGIN
    -- Resolve ledger_id for the new-table inserts (RLS-coherent writes).
    SELECT a.ledger_id INTO v_ledger_id FROM accounts a WHERE a.id = p_account_id;
    IF v_ledger_id IS NULL THEN
        RETURN;
    END IF;

    -- Anchor: balance after the last header strictly before the window.
    -- Ordered by canonical (posted_at, created_at, id) DESC. Falls back
    -- to opening_balance when no prior history.
    SELECT thab.balance_after
      INTO v_starting
      FROM txn_header_account_balances thab
      JOIN txn_headers h ON h.id = thab.header_id
     WHERE thab.account_id = p_account_id
       AND h.posted_at < p_from_posted_at
     ORDER BY h.posted_at DESC, h.created_at DESC, h.id DESC
     LIMIT 1;

    IF v_starting IS NULL THEN
        SELECT a.opening_balance INTO v_starting FROM accounts a WHERE a.id = p_account_id;
    END IF;
    v_starting := COALESCE(v_starting, 0);

    -- Wipe the window for this account. Any header that no longer has a
    -- leg here (account_id moved out via UPDATE) will not be re-inserted;
    -- any header whose net amount changed gets fresh values below.
    DELETE FROM txn_header_account_balances thab
     USING txn_headers h
     WHERE thab.header_id = h.id
       AND thab.account_id = p_account_id
       AND h.posted_at >= p_from_posted_at;

    -- Rebuild: aggregate legs per header (the header-walk), running-SUM
    -- in canonical (posted_at, created_at, id) order, insert.
    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after)
    WITH header_net AS (
        SELECT h.id AS header_id, h.posted_at, h.created_at,
               SUM(l.amount) AS net_amount
          FROM txn_headers h
          JOIN txn_legs l ON l.header_id = h.id
         WHERE l.account_id = p_account_id
           AND h.is_merged_into IS NULL
           AND h.posted_at >= p_from_posted_at
         GROUP BY h.id, h.posted_at, h.created_at
    )
    SELECT
        header_id,
        p_account_id,
        v_ledger_id,
        v_starting + SUM(net_amount) OVER (
            ORDER BY posted_at, created_at, header_id
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        )
      FROM header_net;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION fn_recompute_balances_for_account(UUID, TIMESTAMPTZ) IS
    'ADR-0034: header-walk recompute. For one account, wipe the running '
    'balance window and rebuild by aggregating leg amounts per header, '
    'then running-summing in canonical (posted_at, created_at, id) order.';

-- -----------------------------------------------------------------------------
-- 2) Trigger function on txn_legs (INS / UPD / DEL).
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_trg_legs_recompute_balances()
RETURNS TRIGGER AS $$
DECLARE
    rec RECORD;
BEGIN
    -- The recompute issues DELETE + INSERT on txn_header_account_balances,
    -- not back on txn_legs, so we don't actually risk a recursive fire on
    -- this table. The depth guard remains as defense-in-depth, mirroring
    -- the prior trigger family.
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
        FOR rec IN
            SELECT o.account_id, MIN(h.posted_at) AS dt
              FROM old_rows o
              JOIN txn_headers h ON h.id = o.header_id
             GROUP BY o.account_id
        LOOP
            PERFORM fn_recompute_balances_for_account(rec.account_id, rec.dt);
        END LOOP;
    ELSE  -- UPDATE
        -- Early exit: skip when no balance-relevant column changed.
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

        -- Recompute every account that appears in old or new rows,
        -- anchored at the earliest affected posted_at.
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

-- -----------------------------------------------------------------------------
-- 3) Trigger function on txn_headers (UPDATE) — react to posted_at and
--    is_merged_into changes, which shift every per-account running balance
--    for that header without any leg edit.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_trg_headers_recompute_balances()
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
          JOIN old_rows o ON o.id = n.id
         WHERE n.posted_at      IS DISTINCT FROM o.posted_at
            OR n.is_merged_into IS DISTINCT FROM o.is_merged_into
    ) THEN
        RETURN NULL;
    END IF;

    -- For each affected account, anchor at the EARLIER of old/new
    -- posted_at to cover the case where posted_at moves backwards.
    FOR rec IN
        SELECT l.account_id, MIN(d.dt) AS dt
          FROM (
              SELECT id, posted_at AS dt FROM new_rows
              UNION ALL
              SELECT id, posted_at AS dt FROM old_rows
          ) d
          JOIN txn_legs l ON l.header_id = d.id
         GROUP BY l.account_id
    LOOP
        PERFORM fn_recompute_balances_for_account(rec.account_id, rec.dt);
    END LOOP;

    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- -----------------------------------------------------------------------------
-- 4) Drop the old leg-walk trigger family.
-- -----------------------------------------------------------------------------

DROP TRIGGER IF EXISTS trg_legs_balance_after_insert ON txn_legs;
DROP TRIGGER IF EXISTS trg_legs_balance_after_update ON txn_legs;
DROP TRIGGER IF EXISTS trg_legs_balance_after_delete ON txn_legs;
DROP TRIGGER IF EXISTS trg_headers_balance_after_update ON txn_headers;

DROP FUNCTION IF EXISTS fn_recompute_legs_balance_after(UUID, TIMESTAMPTZ);
DROP FUNCTION IF EXISTS fn_trg_legs_balance_after();
DROP FUNCTION IF EXISTS fn_trg_headers_balance_after();

-- -----------------------------------------------------------------------------
-- 5) Install the new triggers.
-- -----------------------------------------------------------------------------

CREATE TRIGGER trg_legs_recompute_balances_insert
AFTER INSERT ON txn_legs
REFERENCING NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_legs_recompute_balances();

CREATE TRIGGER trg_legs_recompute_balances_update
AFTER UPDATE ON txn_legs
REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_legs_recompute_balances();

CREATE TRIGGER trg_legs_recompute_balances_delete
AFTER DELETE ON txn_legs
REFERENCING OLD TABLE AS old_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_legs_recompute_balances();

CREATE TRIGGER trg_headers_recompute_balances_update
AFTER UPDATE ON txn_headers
REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_headers_recompute_balances();

-- -----------------------------------------------------------------------------
-- 6) One-shot backfill: populate txn_header_account_balances for every
--    account in every ledger.
-- -----------------------------------------------------------------------------
--
-- Approach: iterate distinct (account_id) pairs that have at least one
-- leg, recompute from the dawn of time. Accounts with no legs end up with
-- zero rows in the new table — correct (opening_balance is the entire
-- history, no header has stamped it yet).

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
