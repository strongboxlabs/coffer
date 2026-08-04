-- 159 — Investment money is authoritative at 2 decimals (ADR-0073).
--
-- The API "accept / edit investment transaction" path historically posted the
-- cash + holdings legs as `price * |shares|` UNROUNDED
-- (InvestmentTransactionsRepository.BuildPostings), so a rounded per-share
-- price (e.g. 4.878 sh @ 29.45) left sub-cent amounts on txn_legs
-- (`amount` is numeric(19,4)). Those sub-cent legs then summed into the
-- running balances (txn_header_account_balances is numeric(19,4) too),
-- producing fractional balances that rendered as "-$0.00". At migration time
-- there were 56 such legs.
--
-- The producer is fixed in code: the request `amount` (2dp — the real settled
-- cash) is now authoritative and `unit_price` is DERIVED metadata
-- (amount / |shares|). See ResolveTradeMoney. This migration scrubs the
-- residual sub-cent state and guards against its return:
--   1. round every sub-cent leg amount to 2 decimals (sec / income pairs are
--      ±X, and round() is symmetric about zero, so each pair still nets to 0);
--   2. re-derive unit_price on the affected security legs from the now-2dp
--      amount (÷ |quantity|, 6dp — the register's max display precision, so
--      what is stored equals what is shown);
--   3. rebuild running balances for every affected account and holdings cost
--      basis for every affected ledger (both derive from leg amounts);
--   4. add a 2dp guard so money can never again carry sub-cent fractions.
--
-- NOTE: the 2dp guard encodes the current USD / 2-decimal money model (the
-- app formats all money at 2dp). Multi-currency with non-2dp minor units would
-- revisit this constraint alongside the formatters — TBD, out of scope here.

DO $$
DECLARE
    v_acct    UUID;
    v_ledger  UUID;
    v_accts   UUID[];
    v_ledgers UUID[];
BEGIN
    -- Affected accounts + ledgers, captured BEFORE rounding (afterwards the
    -- sub-cent predicate no longer matches anything).
    SELECT array_agg(DISTINCT tl.account_id),
           array_agg(DISTINCT h.ledger_id)
      INTO v_accts, v_ledgers
      FROM txn_legs tl
      JOIN txn_headers h ON h.id = tl.header_id
     WHERE tl.amount <> round(tl.amount, 2);

    IF v_accts IS NULL THEN
        RAISE NOTICE 'mig 159: no sub-cent legs to scrub';
        RETURN;
    END IF;

    RAISE NOTICE 'mig 159: scrubbing sub-cent legs across % account(s), % ledger(s)',
        array_length(v_accts, 1), array_length(v_ledgers, 1);

    -- 1 + 2. Round money to 2dp and re-derive unit_price on the security legs
    -- from the rounded amount. SET expressions read the OLD row, so
    -- round(amount, 2) inside the unit_price CASE is the new (2dp) amount.
    UPDATE txn_legs
       SET amount = round(amount, 2),
           unit_price = CASE
               WHEN quantity IS NOT NULL AND quantity <> 0
               THEN round(abs(round(amount, 2)) / abs(quantity), 6)
               ELSE unit_price
           END
     WHERE amount <> round(amount, 2);

    -- 3a. Rebuild running balances for each affected account (from dawn of
    -- time — the canonical header-walk recompute, ADR-0034 / mig 090).
    FOREACH v_acct IN ARRAY v_accts LOOP
        PERFORM fn_recompute_balances_for_account(v_acct, '0001-01-01'::timestamptz);
    END LOOP;

    -- 3b. Rebuild holdings cost basis / lots for each affected ledger — lot
    -- unit_cost derives from the (now-rounded) holdings leg amount.
    FOREACH v_ledger IN ARRAY v_ledgers LOOP
        PERFORM recompute_holdings_cost_basis(v_ledger);
    END LOOP;
END $$;

-- 4. Guard: leg money is always exactly 2 decimals. quantity / unit_price keep
-- their own (higher) scale — this constrains the money column only.
ALTER TABLE txn_legs
    ADD CONSTRAINT ck_txn_legs_amount_scale_2
    CHECK (amount = round(amount, 2));

COMMENT ON CONSTRAINT ck_txn_legs_amount_scale_2 ON txn_legs IS
    'ADR-0073: leg money is authoritative at 2 decimals; sub-cent amounts '
    '(historically from price*shares) leak into fractional / "-$0.00" '
    'balances. Producers round to 2dp before insert.';
