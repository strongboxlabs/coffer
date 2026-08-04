-- =============================================================================
-- 056 — posting_role + reframe is_trade_commission as per-brokerage flag (B0.4)
-- =============================================================================
--
-- Migration 054 added `accounts.is_trade_commission` as a per-category
-- flag with an Option B structural gate (fee's cash counterpart must be
-- on an investment account). Migration 055 set it TRUE on top-level
-- "Fees" in Default.
--
-- Both treated the wrong axis. The user's model is:
--   - Category is malleable. The user might assign a fee posting to
--     "Investment Fees" today and "Brokerage Commissions" tomorrow.
--   - The "fee-ness" of a posting is an intent attribute of the
--     posting itself, not derivable from the category it lands on.
--
-- So fee identification moves to a per-leg marker (`posting_role`),
-- and the per-account `is_trade_commission` flag changes semantics:
-- it now lives on the **investment account** (the user-visible
-- brokerage), controlling whether fees on transactions in that account
-- flow into cost basis. A taxable brokerage with real commissions =
-- TRUE; a 401k where in-transaction "fees" are administrative = FALSE
-- (default).
--
-- Going further: posting_role marks all four MD splittypes (sec / inc /
-- xfr / fee), not just fee. The other three are reconstructable from
-- existing schema today, but consistent marking removes inference as
-- a fallback strategy anywhere and makes the data self-describing.
--
-- This migration:
--   1. Adds `txn_legs.posting_role TEXT` with a four-value CHECK.
--   2. Backfills `posting_role` for existing investment legs by
--      heuristic (qty/account_type/category_kind patterns). Going
--      forward, the importer stamps it explicitly from MD's
--      `invest.splittype`.
--   3. Reverts 055's category-level Fees flag (sets
--      is_trade_commission = FALSE on every non-investment account).
--   4. Adds a CHECK constraint: is_trade_commission can be TRUE only
--      on investment accounts.
--   5. Rewrites `recompute_holdings_cost_basis` to use the new
--      two-axis model:
--        - posting_role = 'fee' identifies fee postings (immutable
--          intent marker)
--        - the brokerage's is_trade_commission decides whether those
--          fees flow into basis for that account's holdings
--   6. Re-runs the recompute so existing data converges on the new
--      model. Default flags = FALSE everywhere, so basis values stay
--      identical to post-055 (no commissions flowing into basis until
--      the user flips a brokerage's flag).
-- =============================================================================


-- -----------------------------------------------------------------------------
-- Part 1: Schema — posting_role column
-- -----------------------------------------------------------------------------

ALTER TABLE txn_legs
    ADD COLUMN posting_role TEXT
    CHECK (posting_role IS NULL
           OR posting_role IN ('security', 'income', 'transfer', 'fee'));

COMMENT ON COLUMN txn_legs.posting_role IS
    'Investment posting role marker, immutable to category changes. '
    'One of (security, income, transfer, fee) for investment legs; '
    'NULL for non-investment legs. Both legs of a posting share the '
    'same role. Stamped by the importer from MD''s `invest.splittype` '
    'on import, and by the editor when adding postings. Drives fee '
    'identification in recompute_holdings_cost_basis() + UI rendering '
    '(registry badge, editor field visibility).';


-- -----------------------------------------------------------------------------
-- Part 2: Backfill — heuristic, best-effort for existing data
--
-- The importer will stamp posting_role explicitly going forward. For
-- existing data we recover the role from observable shape:
--   - 'security' — legs in a posting that has a qty-bearing holdings leg
--   - 'fee'      — legs in a posting where the category side is expense,
--                  cash side is brokerage cash, inside an investment header
--   - 'income'   — legs in a posting where the category side is income,
--                  cash side is brokerage cash, inside an investment header
--   - 'transfer' — legs in a posting where both sides are non-category
--                  asset accounts (bank/credit/investment), inside an
--                  investment header
--
-- Both legs of a posting are stamped with the same role. Headers
-- without an investment action (`action IS NULL`) are left alone.
-- -----------------------------------------------------------------------------

DO $$
DECLARE
    v_security_postings INTEGER;
    v_fee_postings INTEGER;
    v_income_postings INTEGER;
    v_transfer_postings INTEGER;
BEGIN
    -- Security postings: any posting that contains a qty-bearing leg.
    WITH sec_postings AS (
        SELECT DISTINCT l.header_id, l.posting_index
        FROM txn_legs l
        JOIN txn_headers h ON h.id = l.header_id
        WHERE h.action IS NOT NULL
          AND l.quantity IS NOT NULL
    )
    UPDATE txn_legs l
    SET posting_role = 'security'
    FROM sec_postings sp
    WHERE l.header_id     = sp.header_id
      AND l.posting_index = sp.posting_index;

    GET DIAGNOSTICS v_security_postings = ROW_COUNT;

    -- Fee postings: same-posting pair where one leg is expense-category
    -- and the counterpart is brokerage-cash (account_type='investment'
    -- with a holdings_account_id pointer — distinguishes brokerage from
    -- the Holdings sibling).
    WITH fee_postings AS (
        SELECT DISTINCT l_cat.header_id, l_cat.posting_index
        FROM txn_legs l_cat
        JOIN accounts a_cat ON a_cat.id = l_cat.account_id
        JOIN txn_legs l_cash ON l_cash.header_id     = l_cat.header_id
                            AND l_cash.posting_index = l_cat.posting_index
                            AND l_cash.id           <> l_cat.id
        JOIN accounts a_cash ON a_cash.id = l_cash.account_id
        JOIN txn_headers h ON h.id = l_cat.header_id
        WHERE h.action IS NOT NULL
          AND a_cat.account_type    = 'category'
          AND a_cat.category_kind   = 'expense'
          AND a_cash.account_type   = 'investment'
          AND a_cash.holdings_account_id IS NOT NULL
    )
    UPDATE txn_legs l
    SET posting_role = 'fee'
    FROM fee_postings fp
    WHERE l.header_id     = fp.header_id
      AND l.posting_index = fp.posting_index
      AND l.posting_role IS NULL;  -- don't overwrite 'security' on overlapping shapes

    GET DIAGNOSTICS v_fee_postings = ROW_COUNT;

    -- Income postings: same-posting pair where one leg is income-category
    -- and the counterpart is brokerage-cash.
    WITH inc_postings AS (
        SELECT DISTINCT l_cat.header_id, l_cat.posting_index
        FROM txn_legs l_cat
        JOIN accounts a_cat ON a_cat.id = l_cat.account_id
        JOIN txn_legs l_cash ON l_cash.header_id     = l_cat.header_id
                            AND l_cash.posting_index = l_cat.posting_index
                            AND l_cash.id           <> l_cat.id
        JOIN accounts a_cash ON a_cash.id = l_cash.account_id
        JOIN txn_headers h ON h.id = l_cat.header_id
        WHERE h.action IS NOT NULL
          AND a_cat.account_type    = 'category'
          AND a_cat.category_kind   = 'income'
          AND a_cash.account_type   = 'investment'
          AND a_cash.holdings_account_id IS NOT NULL
    )
    UPDATE txn_legs l
    SET posting_role = 'income'
    FROM inc_postings ip
    WHERE l.header_id     = ip.header_id
      AND l.posting_index = ip.posting_index
      AND l.posting_role IS NULL;

    GET DIAGNOSTICS v_income_postings = ROW_COUNT;

    -- Transfer postings: both legs are non-category asset accounts in an
    -- investment header. Excludes pairs we've already labelled
    -- (security/fee/income).
    WITH xfr_postings AS (
        SELECT DISTINCT l1.header_id, l1.posting_index
        FROM txn_legs l1
        JOIN accounts a1 ON a1.id = l1.account_id
        JOIN txn_legs l2 ON l2.header_id     = l1.header_id
                        AND l2.posting_index = l1.posting_index
                        AND l2.id           <> l1.id
        JOIN accounts a2 ON a2.id = l2.account_id
        JOIN txn_headers h ON h.id = l1.header_id
        WHERE h.action IS NOT NULL
          AND a1.account_type IN ('bank', 'credit_card', 'investment')
          AND a2.account_type IN ('bank', 'credit_card', 'investment')
    )
    UPDATE txn_legs l
    SET posting_role = 'transfer'
    FROM xfr_postings xp
    WHERE l.header_id     = xp.header_id
      AND l.posting_index = xp.posting_index
      AND l.posting_role IS NULL;

    GET DIAGNOSTICS v_transfer_postings = ROW_COUNT;

    RAISE NOTICE 'Migration 056 backfill: security=% fee=% income=% transfer=% (leg counts, both sides per posting)',
        v_security_postings, v_fee_postings, v_income_postings, v_transfer_postings;
END;
$$;


-- -----------------------------------------------------------------------------
-- Part 3: Reframe is_trade_commission — narrow to investment accounts only.
--
-- Migration 054 added the column with no type constraint. Migration 055
-- set it TRUE on a category. Migration 056 reverts the semantic: clear
-- the flag on every non-investment account, then constrain via CHECK.
-- -----------------------------------------------------------------------------

UPDATE accounts
SET is_trade_commission = FALSE
WHERE is_trade_commission = TRUE
  AND account_type <> 'investment';

ALTER TABLE accounts
    ADD CONSTRAINT accounts_is_trade_commission_only_on_investment
    CHECK (is_trade_commission = FALSE OR account_type = 'investment');

COMMENT ON COLUMN accounts.is_trade_commission IS
    'On an investment (brokerage) account: when TRUE, fee-marked '
    'postings (`txn_legs.posting_role = ''fee''`) in that account''s '
    'transactions flow into cost basis. Defaults FALSE. Set TRUE for '
    'taxable brokerages where in-transaction fees are real commissions; '
    'leave FALSE for 401k-style accounts where "fees" are administrative '
    'deductions, not part of cost basis. CHECK constraint enforces that '
    'the flag is only meaningful on `account_type=''investment''` rows.';


-- -----------------------------------------------------------------------------
-- Part 4: Rewrite recompute_holdings_cost_basis() for the new two-axis
-- model: posting_role identifies fees; brokerage's flag policy controls
-- whether they flow into basis.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(p_ledger_id UUID DEFAULT NULL)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_holding RECORD;
    v_leg RECORD;
    v_lot RECORD;
    v_brokerage_include_fees BOOLEAN;
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
        -- Look up the brokerage's commission policy. holdings.account_id
        -- is the Holdings sibling; the brokerage is the account whose
        -- holdings_account_id = that sibling. is_trade_commission on
        -- the brokerage (= TRUE) means fees flow into basis here.
        SELECT COALESCE(b.is_trade_commission, FALSE)
        INTO v_brokerage_include_fees
        FROM accounts b
        WHERE b.holdings_account_id = v_holding.account_id;

        -- Defensive: if no brokerage owns this sibling (shouldn't
        -- happen post-049's structural FKs), treat as FALSE.
        v_brokerage_include_fees := COALESCE(v_brokerage_include_fees, FALSE);

        -- Idempotency reset: restore every lot for this holding to its
        -- acquired state with a freshly-computed unit_cost.
        UPDATE lots l
        SET quantity  = tl.quantity,
            is_closed = FALSE,
            unit_cost = CASE
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
                IF v_brokerage_include_fees THEN
                    -- Fee inclusion: sum amounts from fee-marked legs
                    -- in the same header. posting_role is the source of
                    -- truth; category assignment is irrelevant.
                    v_fee := COALESCE((
                        SELECT SUM(fl.amount)
                        FROM txn_legs fl
                        WHERE fl.header_id    = v_leg.header_id
                          AND fl.posting_role = 'fee'
                          AND fl.amount > 0
                    ), 0);
                ELSE
                    v_fee := 0;
                END IF;
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

COMMENT ON FUNCTION recompute_holdings_cost_basis(UUID) IS
    'Avg-cost basis + FIFO lot closure with two-axis fee model '
    '(migration 056). For each holding: (1) reads the brokerage''s '
    'is_trade_commission flag to decide whether fees flow into basis; '
    '(2) walks holdings-side legs in posted_at order; (3) on '
    'acquisitions, basis += leg.amount + (sum of posting_role=''fee'' '
    'amounts in same header, gated by brokerage flag); (4) on '
    'dispositions, applies avg-cost reduction + FIFO lot closure. '
    'Idempotent: resets lot state from txn_legs at the start of each '
    'holding so re-runs converge.';


-- -----------------------------------------------------------------------------
-- Part 5: Re-run the recompute so existing data converges on the new model.
--
-- With the default-FALSE is_trade_commission flag everywhere, results
-- should be identical to post-055 — fees don't flow into basis until
-- the user explicitly flips a brokerage's flag.
-- -----------------------------------------------------------------------------

SELECT recompute_holdings_cost_basis(NULL);
