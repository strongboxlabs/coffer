-- =============================================================================
-- 068 — DB-owned recompute via triggers (eliminates inline SQL from the API)
-- =============================================================================
--
-- THE STRUCTURAL FIX
--
-- Migration 067 narrowed `recompute_holdings_cost_basis` so a single
-- save could touch one holding instead of N. But the call still
-- originated from inline SQL inside the API repositories
-- (`InvestmentTransactionsRepository.Create/Patch/DeleteAsync` and
-- `AccountsRepository.SetIsTradeCommissionAsync`), which violates
-- the no-raw-sql-in-API policy (memory `feedback_no_raw_sql_in_api`).
--
-- This migration moves the recompute trigger to the DB layer:
--
--   1. `txn_legs` AFTER INSERT/UPDATE/DELETE FOR EACH STATEMENT —
--      walks transition tables, collects distinct (sibling_account,
--      security) pairs, calls the narrow recompute for each.
--      Fires whether legs come from the investment API, the bank
--      API, or the importer. Bank legs carry `security_id IS NULL`
--      and are filtered out — no cost on bank writes.
--
--   2. `accounts` AFTER UPDATE OF is_trade_commission FOR EACH ROW —
--      a brokerage flag flip changes fee→basis flow for every
--      holding under it; recompute every (brokerage, security) on
--      that account.
--
-- The recompute function gains a "create holding if missing" path:
-- when called narrowly with (account, security) that has no holding
-- row yet, INSERT one with zero placeholders, then walk legs and
-- write authoritative values. This decouples the trigger from the
-- order in which EF flushes inserts (txn_legs vs holdings), and
-- lets the API stop calling `UpsertHoldings` for its own purposes
-- (lots still need the holding_id, so the repo will query the
-- post-INSERT row instead of inserting it).
--
-- C# side (separate commit, same PR):
--   - Drop the three `ExecuteSqlInterpolatedAsync` recompute calls
--     in `InvestmentTransactionsRepository`
--   - Drop the helper `RecomputeNarrowAsync` + the scope capture
--   - Drop the call in `AccountsRepository.SetIsTradeCommissionAsync`
--     + the `APPROVED-RAW-SQL-EXCEPTION` tag above it
--   - Update `UpsertHoldingsAsync` to fall through to a SELECT after
--     SaveChanges if the holding was created by the trigger
--
-- Importer side: the importer's end-of-import full-ledger scrub
-- still works (calls function with all NULL params) but is now
-- redundant. Left in place for one slice; can be removed in a
-- follow-up after a clean import demonstrates the triggers carry
-- the load.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- Part 1: extend the recompute function to auto-create holdings.
--
-- Same body as 067; only the loop's WHERE clause gains the INSERT
-- side. The 3-param signature stays.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(
    p_ledger_id   UUID DEFAULT NULL,
    p_account_id  UUID DEFAULT NULL,
    p_security_id UUID DEFAULT NULL
)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_holding RECORD;
    v_event RECORD;
    v_lot RECORD;
    v_brokerage_include_fees BOOLEAN;
    v_running_qty NUMERIC;
    v_running_basis NUMERIC;
    v_avg_cost NUMERIC;
    v_fee NUMERIC;
    v_remaining_sell NUMERIC;
    v_updated INTEGER := 0;
    v_resolved_ledger_id UUID;
BEGIN
    -- Auto-create the holding row when the caller pinned a specific
    -- (account, security) but no row exists yet. Triggers on
    -- txn_legs INSERT call us with the new leg's (sibling, security)
    -- before the API's UpsertHoldings has flushed; without this
    -- step the holding row would never get its authoritative
    -- cost_basis written until a second trigger pass.
    IF p_account_id IS NOT NULL AND p_security_id IS NOT NULL THEN
        SELECT ledger_id INTO v_resolved_ledger_id
        FROM accounts WHERE id = p_account_id;

        IF v_resolved_ledger_id IS NOT NULL
           AND (p_ledger_id IS NULL OR p_ledger_id = v_resolved_ledger_id)
           AND NOT EXISTS (
               SELECT 1 FROM holdings
               WHERE account_id  = p_account_id
                 AND security_id = p_security_id
           )
        THEN
            INSERT INTO holdings (id, account_id, security_id, ledger_id, quantity, cost_basis, as_of)
            VALUES (gen_random_uuid(), p_account_id, p_security_id, v_resolved_ledger_id, 0, 0, NOW());
        END IF;
    END IF;

    FOR v_holding IN
        SELECT id, account_id, security_id, ledger_id
        FROM holdings
        WHERE (p_ledger_id   IS NULL OR ledger_id   = p_ledger_id)
          AND (p_account_id  IS NULL OR account_id  = p_account_id)
          AND (p_security_id IS NULL OR security_id = p_security_id)
    LOOP
        SELECT COALESCE(b.is_trade_commission, FALSE)
        INTO v_brokerage_include_fees
        FROM accounts b
        WHERE b.holdings_account_id = v_holding.account_id;
        v_brokerage_include_fees := COALESCE(v_brokerage_include_fees, FALSE);

        UPDATE lots l
        SET quantity  = tl.quantity,
            is_closed = FALSE,
            unit_cost = CASE
                WHEN tl.quantity = 0 THEN 0
                WHEN v_brokerage_include_fees THEN
                    (tl.amount + COALESCE((
                        SELECT SUM(fl.amount)
                        FROM txn_legs fl
                        WHERE fl.header_id    = tl.header_id
                          AND fl.posting_role = 'fee'
                          AND fl.amount > 0
                    ), 0)) / tl.quantity
                ELSE
                    tl.amount / tl.quantity
            END
        FROM txn_legs tl
        WHERE l.holding_id = v_holding.id
          AND l.leg_id     = tl.id;

        v_running_qty   := 0;
        v_running_basis := 0;

        FOR v_event IN
            SELECT
                'leg'::TEXT AS kind,
                hd.posted_at AS event_at,
                l.id AS leg_id,
                l.header_id,
                l.amount,
                l.quantity,
                NULL::NUMERIC AS ratio
            FROM txn_legs l
            JOIN txn_headers hd ON hd.id = l.header_id
            WHERE l.security_id = v_holding.security_id
              AND l.account_id  = v_holding.account_id
              AND l.quantity IS NOT NULL

            UNION ALL

            SELECT
                'split'::TEXT AS kind,
                ss.split_at AS event_at,
                NULL::UUID AS leg_id,
                NULL::UUID AS header_id,
                NULL::NUMERIC AS amount,
                NULL::NUMERIC AS quantity,
                ss.ratio
            FROM security_splits ss
            WHERE ss.security_id = v_holding.security_id
              AND ss.ledger_id   = v_holding.ledger_id

            ORDER BY event_at, kind, leg_id
        LOOP
            IF v_event.kind = 'split' THEN
                v_running_qty := v_running_qty * v_event.ratio;

                UPDATE lots
                SET quantity = quantity * v_event.ratio
                WHERE holding_id = v_holding.id
                  AND is_closed  = FALSE;

            ELSIF v_event.quantity > 0 THEN
                IF v_brokerage_include_fees THEN
                    v_fee := COALESCE((
                        SELECT SUM(fl.amount)
                        FROM txn_legs fl
                        WHERE fl.header_id    = v_event.header_id
                          AND fl.posting_role = 'fee'
                          AND fl.amount > 0
                    ), 0);
                ELSE
                    v_fee := 0;
                END IF;
                v_running_qty   := v_running_qty + v_event.quantity;
                v_running_basis := v_running_basis + v_event.amount + v_fee;

            ELSIF v_event.quantity < 0 AND v_running_qty > 0 THEN
                v_avg_cost      := v_running_basis / v_running_qty;
                v_running_basis := v_running_basis - (v_avg_cost * ABS(v_event.quantity));
                v_running_qty   := v_running_qty + v_event.quantity;
                IF v_running_qty <= 0 THEN
                    v_running_qty   := 0;
                    v_running_basis := 0;
                END IF;

                v_remaining_sell := ABS(v_event.quantity);
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
        SET cost_basis = v_running_basis,
            quantity   = v_running_qty
        WHERE id = v_holding.id;

        v_updated := v_updated + 1;
    END LOOP;

    RETURN v_updated;
END;
$$;


-- -----------------------------------------------------------------------------
-- Part 2: trigger function — walk transition tables, dispatch narrow recompute.
--
-- One function reused by three triggers (AFTER INSERT / AFTER UPDATE /
-- AFTER DELETE on txn_legs). The transition table name differs per
-- operation, so each trigger passes its own; the function reads the
-- right one via TG_OP.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION trg_txn_legs_recompute_holdings()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_pair RECORD;
BEGIN
    -- Collect distinct (account_id, security_id) pairs from the
    -- transition table relevant to this operation. INSERT + UPDATE
    -- reference NEW; DELETE references OLD; UPDATE fires twice
    -- (separate OLD + NEW triggers, see Part 3) so a leg moving
    -- between holdings reconciles both ends.
    --
    -- `account_id` on the leg IS `holdings.account_id` (the
    -- system-managed Holdings sibling — ADR-0019). Pass it through
    -- to the recompute function directly. The function looks the
    -- brokerage up via accounts.holdings_account_id when it needs
    -- the is_trade_commission flag.
    FOR v_pair IN
        SELECT DISTINCT account_id, security_id
        FROM dirty_legs
        WHERE security_id IS NOT NULL AND quantity IS NOT NULL
    LOOP
        PERFORM recompute_holdings_cost_basis(
            NULL, v_pair.account_id, v_pair.security_id);
    END LOOP;

    RETURN NULL;
END;
$$;


-- -----------------------------------------------------------------------------
-- Part 3: three triggers on txn_legs, one per operation.
--
-- AFTER STATEMENT firing — runs once per INSERT/UPDATE/DELETE
-- statement regardless of row count. The trigger function reads from
-- a uniformly-named `dirty_legs` transition table, populated by each
-- trigger from NEW/OLD as appropriate. UPDATE adds both so a leg
-- moving between (account, security) pairs reconciles both ends.
-- -----------------------------------------------------------------------------

DROP TRIGGER IF EXISTS trg_txn_legs_recompute_insert ON txn_legs;
CREATE TRIGGER trg_txn_legs_recompute_insert
    AFTER INSERT ON txn_legs
    REFERENCING NEW TABLE AS dirty_legs
    FOR EACH STATEMENT
    EXECUTE FUNCTION trg_txn_legs_recompute_holdings();

DROP TRIGGER IF EXISTS trg_txn_legs_recompute_delete ON txn_legs;
CREATE TRIGGER trg_txn_legs_recompute_delete
    AFTER DELETE ON txn_legs
    REFERENCING OLD TABLE AS dirty_legs
    FOR EACH STATEMENT
    EXECUTE FUNCTION trg_txn_legs_recompute_holdings();

-- UPDATE: two passes — once for OLD (to reconcile the holding the
-- leg may have left), once for NEW (to reconcile the holding it
-- joined / stayed on). Cheaper than a single union here because each
-- trigger fires its own statement with its own transition table.
DROP TRIGGER IF EXISTS trg_txn_legs_recompute_update_old ON txn_legs;
CREATE TRIGGER trg_txn_legs_recompute_update_old
    AFTER UPDATE ON txn_legs
    REFERENCING OLD TABLE AS dirty_legs
    FOR EACH STATEMENT
    EXECUTE FUNCTION trg_txn_legs_recompute_holdings();

DROP TRIGGER IF EXISTS trg_txn_legs_recompute_update_new ON txn_legs;
CREATE TRIGGER trg_txn_legs_recompute_update_new
    AFTER UPDATE ON txn_legs
    REFERENCING NEW TABLE AS dirty_legs
    FOR EACH STATEMENT
    EXECUTE FUNCTION trg_txn_legs_recompute_holdings();


-- -----------------------------------------------------------------------------
-- Part 4: trigger on accounts.is_trade_commission flip.
--
-- Per migration 056, the brokerage's is_trade_commission flag drives
-- whether posting_role='fee' amounts flow into basis. Changing the
-- flag retroactively changes every holding under that brokerage —
-- recompute the whole account.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION trg_accounts_is_trade_commission_recompute()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    -- NEW is the brokerage row; holdings.account_id is the Holdings
    -- sibling. Resolve via NEW.holdings_account_id, then recompute
    -- every holding on that sibling (one row per security held).
    IF NEW.holdings_account_id IS NOT NULL THEN
        PERFORM recompute_holdings_cost_basis(
            NULL, NEW.holdings_account_id, NULL);
    END IF;
    RETURN NULL;
END;
$$;

DROP TRIGGER IF EXISTS trg_accounts_recompute_on_commission_flip ON accounts;
CREATE TRIGGER trg_accounts_recompute_on_commission_flip
    AFTER UPDATE OF is_trade_commission ON accounts
    FOR EACH ROW
    WHEN (OLD.is_trade_commission IS DISTINCT FROM NEW.is_trade_commission)
    EXECUTE FUNCTION trg_accounts_is_trade_commission_recompute();


-- -----------------------------------------------------------------------------
-- Part 5: comments.
-- -----------------------------------------------------------------------------

COMMENT ON FUNCTION recompute_holdings_cost_basis(UUID, UUID, UUID) IS
    'Avg-cost recompute (060/063/067/068). Walks the unified '
    '(txn_legs union security_splits) event stream per holding in '
    'chronological order. Reads brokerage is_trade_commission (056) '
    'for fee→basis decisions. From 068: auto-creates a holdings row '
    'when called narrowly with (account, security) that has no row '
    'yet, so DB triggers on txn_legs can drive recompute without '
    'depending on EF insert order.';

COMMENT ON FUNCTION trg_txn_legs_recompute_holdings() IS
    'Statement-level trigger (068): reads dirty_legs transition '
    'table, collects distinct (sibling_account, security) pairs with '
    'security_id NOT NULL, looks up the brokerage via '
    'accounts.holdings_account_id, calls recompute narrowly. Fires '
    'whether legs come from the API or the importer.';

COMMENT ON FUNCTION trg_accounts_is_trade_commission_recompute() IS
    'Row-level trigger (068): on is_trade_commission flip, recompute '
    'every holding under the brokerage. Replaces inline SQL in '
    'AccountsRepository.SetIsTradeCommissionAsync.';
