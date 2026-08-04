-- 129_balance_self_ref_buysellxfr_legs.sql
--
-- Scrub the self-referential buysellxfr (sellx / buyx) headers the importer
-- left UNBALANCED, then re-derive the affected balances. See ADR-0053.
--
-- Root cause: InvestmentTransactionMapper zeroed the brokerage cash on a
-- self-referential buysellxfr ("MD nets the cash to zero"), dropping the
-- sale proceeds and leaving the header unbalanced by the trade amount.
-- The netting actually happens across the PAIRED buy / fee leg (a separate
-- header / split), not within this one header. The mapper is fixed going
-- forward (it now books the proceeds); this corrects the rows already
-- written -- a batch on the Default ledger's closed brokerage account,
-- whose cash balance reads negative by EXACTLY the sum of the dropped
-- proceeds. Booking them brings that account to zero (a wound-down account).
--
-- Mechanism: for each affected header the sec-pair brokerage-cash leg
-- (amount 0, paired by posting_index with the share-bearing holdings leg)
-- is set to the proceeds (-holdings.amount), balancing the header. Only
-- `amount` changes, so the mig-057 posting_role trigger is satisfied.
-- Then balances recompute for every account the scrub touched. The block
-- RAISEs if any sellx / buyx header remains unbalanced afterwards.
--
-- Idempotent: after the scrub no imbalanced sellx / buyx header remains,
-- so a re-run matches nothing. Global (all ledgers) by design, but only
-- rows produced by the pre-fix mapper qualify.

BEGIN;

DO $$
DECLARE
    rec RECORD;
    v_fixed INTEGER;
    v_headers INTEGER;
BEGIN
    -- Target: sellx / buyx headers whose legs do not sum to zero.
    CREATE TEMP TABLE _bal_targets ON COMMIT DROP AS
        SELECT h.id AS header_id
        FROM txn_headers h
        JOIN txn_legs l ON l.header_id = h.id
        WHERE h.action IN ('sellx', 'buyx')
        GROUP BY h.id
        HAVING ROUND(SUM(l.amount), 2) <> 0;

    SELECT COUNT(*) INTO v_headers FROM _bal_targets;

    -- Book the dropped proceeds onto the zeroed sec-pair cash leg.
    UPDATE txn_legs cash
       SET amount = -hold.amount
      FROM txn_legs hold
     WHERE cash.header_id     = hold.header_id
       AND cash.posting_index = hold.posting_index
       AND cash.id           <> hold.id
       AND hold.quantity IS NOT NULL AND hold.quantity <> 0   -- the holdings leg
       AND cash.quantity IS NULL                              -- the cash leg
       AND cash.amount = 0
       AND cash.header_id IN (SELECT header_id FROM _bal_targets);
    GET DIAGNOSTICS v_fixed = ROW_COUNT;
    RAISE NOTICE 'mig 129: booked proceeds on % cash leg(s) across % header(s)', v_fixed, v_headers;

    -- Re-derive running balances for every account the scrub touched.
    FOR rec IN
        SELECT DISTINCT l.account_id
        FROM txn_legs l
        WHERE l.header_id IN (SELECT header_id FROM _bal_targets)
    LOOP
        PERFORM fn_recompute_balances_for_account(rec.account_id, '0001-01-01'::timestamptz);
    END LOOP;

    -- Self-verify: no sellx / buyx header may remain unbalanced.
    IF EXISTS (
        SELECT 1
        FROM txn_headers h
        JOIN txn_legs l ON l.header_id = h.id
        WHERE h.action IN ('sellx', 'buyx')
        GROUP BY h.id
        HAVING ROUND(SUM(l.amount), 2) <> 0
    ) THEN
        RAISE EXCEPTION 'mig 129: sellx / buyx header(s) still unbalanced after scrub';
    END IF;
END $$;

COMMIT;
