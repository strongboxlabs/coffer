-- =============================================================================
-- 055 — Demo ledger isolation + Fees commission flag (slice B0.3)
-- =============================================================================
--
-- Three related changes that ride together because they all stem from
-- the same observation: the dev "Default" ledger has been mixing real
-- user data with three independent seed scripts' demo/test/synthetic
-- rows, and that mixing produces user-visible noise (e.g. the IDXA
-- stock-split row from seed-demo-investments.sql leaks a 100-share
-- discrepancy into the real holdings view).
--
-- 1. Function update — `recompute_holdings_cost_basis` now also
--    refreshes `lots.unit_cost` on the idempotency-reset step. Before:
--    the function reset `lots.quantity` and `lots.is_closed` from the
--    source `txn_legs` row but left `unit_cost` at whatever the last
--    rebuild produced. So flipping `is_trade_commission` and calling
--    the function updated `holdings.cost_basis` but NOT `lots.unit_cost`
--    — basis and per-lot prices could drift apart. Fixed by computing
--    unit_cost in the reset, with the same Option B-gated commission
--    inclusion as the lots rebuild.
--
-- 2. Demo ledger isolation — every `(test)`, `(demo)`, and
--    `(synthetic)` account in Default ledger is deleted, with their
--    txn_headers, txn_legs, holdings, and lots removed via the same
--    cascade. A new "Demo" ledger is created with id
--    `00000000-0000-0000-0000-000000000002` and a grant for the first
--    user (matching Default's grant shape). The three seed scripts
--    (scripts/seed-demo-investments.sql,
--    scripts/seed-demo-similar-merge.sql, scripts/seed-synthetic-2500.sql)
--    are rewired in lockstep to target this Demo ledger; re-running
--    them after this migration populates Demo without touching Default.
--
-- 3. Fees commission flag — in Default ledger, the "Fees" expense
--    category gets `is_trade_commission = TRUE`. This category was
--    used for a handful of legitimate options-trading commissions over
--    several years (a small total). The recompute pass at the end of this
--    migration folds those commissions into basis. "Investment Fees"
--    stays FALSE because its Buy-side rows are mostly a few
--    reconciliation oddities + tiny test rows, not real commissions.
-- =============================================================================


-- -----------------------------------------------------------------------------
-- Part 1: Function update — recompute refreshes lots.unit_cost too.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(p_ledger_id UUID DEFAULT NULL)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_holding RECORD;
    v_leg RECORD;
    v_lot RECORD;
    v_running_qty NUMERIC;
    v_running_basis NUMERIC;
    v_avg_cost NUMERIC;
    v_fee NUMERIC;
    v_remaining_sell NUMERIC;
    v_updated INTEGER := 0;
BEGIN
    FOR v_holding IN
        SELECT id, account_id, security_id
        FROM holdings
        WHERE p_ledger_id IS NULL OR ledger_id = p_ledger_id
    LOOP
        -- Idempotency reset: restore every lot for this holding to its
        -- acquired state. This now includes a recomputed unit_cost so
        -- flag flips on is_trade_commission propagate to per-lot prices
        -- on the next function call, not just to holdings.cost_basis.
        --
        -- unit_cost formula matches the lots rebuild in 054:
        --   (leg.amount + commission) / leg.quantity
        -- with commission gated by:
        --   - fee_category.is_trade_commission = TRUE
        --   - fee leg's same-posting counterpart on an investment account
        --     (Option B structural gate)
        UPDATE lots l
        SET quantity  = tl.quantity,
            is_closed = FALSE,
            unit_cost = (tl.amount + COALESCE((
                SELECT SUM(fl.amount)
                FROM txn_legs fl
                JOIN accounts fa ON fa.id = fl.account_id
                WHERE fl.header_id = tl.header_id
                  AND fa.account_type = 'category'
                  AND fa.is_trade_commission = TRUE
                  AND EXISTS (
                      SELECT 1
                      FROM txn_legs sl
                      JOIN accounts sa ON sa.id = sl.account_id
                      WHERE sl.header_id     = fl.header_id
                        AND sl.posting_index = fl.posting_index
                        AND sl.id           <> fl.id
                        AND sa.account_type  = 'investment'
                  )
            ), 0)) / tl.quantity
        FROM txn_legs tl
        WHERE l.holding_id = v_holding.id
          AND l.leg_id     = tl.id;

        v_running_qty   := 0;
        v_running_basis := 0;

        FOR v_leg IN
            SELECT l.amount, l.quantity, l.header_id
            FROM txn_legs l
            JOIN txn_headers hd ON hd.id = l.header_id
            WHERE l.security_id = v_holding.security_id
              AND l.account_id  = v_holding.account_id
              AND l.quantity IS NOT NULL
            ORDER BY hd.posted_at, l.id
        LOOP
            IF v_leg.quantity > 0 THEN
                v_fee := COALESCE((
                    SELECT SUM(fl.amount)
                    FROM txn_legs fl
                    JOIN accounts fa ON fa.id = fl.account_id
                    WHERE fl.header_id = v_leg.header_id
                      AND fa.account_type = 'category'
                      AND fa.is_trade_commission = TRUE
                      AND EXISTS (
                          SELECT 1
                          FROM txn_legs sl
                          JOIN accounts sa ON sa.id = sl.account_id
                          WHERE sl.header_id     = fl.header_id
                            AND sl.posting_index = fl.posting_index
                            AND sl.id           <> fl.id
                            AND sa.account_type  = 'investment'
                      )
                ), 0);
                v_running_qty   := v_running_qty + v_leg.quantity;
                v_running_basis := v_running_basis + v_leg.amount + v_fee;

            ELSIF v_leg.quantity < 0 AND v_running_qty > 0 THEN
                v_avg_cost      := v_running_basis / v_running_qty;
                v_running_basis := v_running_basis - (v_avg_cost * ABS(v_leg.quantity));
                v_running_qty   := v_running_qty + v_leg.quantity;
                IF v_running_qty <= 0 THEN
                    v_running_qty   := 0;
                    v_running_basis := 0;
                END IF;

                v_remaining_sell := ABS(v_leg.quantity);
                FOR v_lot IN
                    SELECT id, quantity
                    FROM lots
                    WHERE holding_id = v_holding.id
                      AND is_closed  = FALSE
                      AND quantity   > 0
                    ORDER BY acquired_at, id
                LOOP
                    EXIT WHEN v_remaining_sell <= 0;

                    IF v_lot.quantity <= v_remaining_sell THEN
                        UPDATE lots
                        SET quantity  = 0,
                            is_closed = TRUE
                        WHERE id = v_lot.id;
                        v_remaining_sell := v_remaining_sell - v_lot.quantity;
                    ELSE
                        UPDATE lots
                        SET quantity = quantity - v_remaining_sell
                        WHERE id = v_lot.id;
                        v_remaining_sell := 0;
                    END IF;
                END LOOP;
            END IF;
        END LOOP;

        UPDATE holdings
        SET cost_basis = v_running_basis
        WHERE id = v_holding.id;

        v_updated := v_updated + 1;
    END LOOP;

    RETURN v_updated;
END;
$$;


-- -----------------------------------------------------------------------------
-- Part 2: Delete demo / test / synthetic rows from Default ledger.
--
-- 12 accounts identified by name pattern. Deletion order matters:
-- lots → holdings → txn_legs → txn_headers → accounts. The seeds were
-- self-contained (every header has only demo-account legs or — for
-- synthetic — a Default-category counterpart that we remove the header
-- for entirely), so deleting headers that touch a demo account is safe.
-- -----------------------------------------------------------------------------

DO $$
DECLARE
    v_demo_account_ids UUID[];
    v_demo_header_ids UUID[];
BEGIN
    SELECT ARRAY_AGG(id) INTO v_demo_account_ids
    FROM accounts
    WHERE ledger_id = '00000000-0000-0000-0000-000000000001'
      AND (name LIKE '%(test)%'
           OR name LIKE '%(demo)%'
           OR name ILIKE '%(synthetic)%');

    IF v_demo_account_ids IS NULL OR array_length(v_demo_account_ids, 1) = 0 THEN
        RAISE NOTICE 'Migration 055: no demo accounts to clean up.';
        RETURN;
    END IF;

    -- Headers whose legs touch any demo account. A synthetic header
    -- has one leg on Checking (synthetic) + one on a real category;
    -- this catches both kinds.
    SELECT ARRAY_AGG(DISTINCT header_id) INTO v_demo_header_ids
    FROM txn_legs
    WHERE account_id = ANY(v_demo_account_ids);

    -- 1. Lots first (their holding_id FK is RESTRICT).
    DELETE FROM lots
    WHERE holding_id IN (
        SELECT id FROM holdings WHERE account_id = ANY(v_demo_account_ids)
    );

    -- 2. Holdings.
    DELETE FROM holdings WHERE account_id = ANY(v_demo_account_ids);

    -- 3. Txn legs of demo-tied headers (covers both demo legs and
    --    real-category counterparts on synthetic headers).
    IF v_demo_header_ids IS NOT NULL THEN
        DELETE FROM txn_legs WHERE header_id = ANY(v_demo_header_ids);
        -- 4. Header overrides + tag attachments.
        DELETE FROM txn_header_overrides WHERE header_id = ANY(v_demo_header_ids);
        DELETE FROM txn_header_tags      WHERE header_id = ANY(v_demo_header_ids);
        -- 5. Headers.
        DELETE FROM txn_headers WHERE id = ANY(v_demo_header_ids);
    END IF;

    -- 6. Finally the accounts themselves.
    DELETE FROM accounts WHERE id = ANY(v_demo_account_ids);

    RAISE NOTICE 'Migration 055: deleted % demo accounts and % associated headers from Default ledger.',
        array_length(v_demo_account_ids, 1),
        COALESCE(array_length(v_demo_header_ids, 1), 0);
END;
$$;


-- -----------------------------------------------------------------------------
-- Part 3: Create the Demo ledger (idempotent) + propagate user grants.
--
-- Fixed id so seed scripts can reference it without lookup. Grants are
-- mirrored from Default — every user with access to Default also gets
-- the same role on Demo, so the UI surfaces Demo immediately after the
-- migration.
-- -----------------------------------------------------------------------------

INSERT INTO ledgers (id, name)
VALUES ('00000000-0000-0000-0000-000000000002', 'Demo')
ON CONFLICT (id) DO NOTHING;

INSERT INTO user_ledger_grants (user_id, ledger_id, role)
SELECT user_id, '00000000-0000-0000-0000-000000000002'::uuid, role
FROM user_ledger_grants
WHERE ledger_id = '00000000-0000-0000-0000-000000000001'
ON CONFLICT DO NOTHING;


-- -----------------------------------------------------------------------------
-- Part 4: Flag the Default ledger's "Fees" category as a trade
-- commission. This is the bucket the user used for legitimate
-- options-trading commissions over several years; the Option B gate
-- ensures only events where the fee's cash counterpart is on an
-- investment account contribute to basis.
-- -----------------------------------------------------------------------------

-- Top-level "Fees" only — `parent_id IS NULL` filters out
-- sub-categories like "Education > Fees" that happen to share the
-- name but are not investment commissions. (Even without this filter
-- Option B's structural gate would block pollution from non-investment
-- paths, but the narrower UPDATE keeps the audit trail honest.)
UPDATE accounts
SET is_trade_commission = TRUE
WHERE ledger_id = '00000000-0000-0000-0000-000000000001'
  AND account_type = 'category'
  AND category_kind = 'expense'
  AND name = 'Fees'
  AND parent_id IS NULL;


-- -----------------------------------------------------------------------------
-- Part 5: Re-run recompute over every ledger so the new unit_cost
-- refresh AND the new commission flag take effect on existing data.
-- -----------------------------------------------------------------------------

SELECT recompute_holdings_cost_basis(NULL);
