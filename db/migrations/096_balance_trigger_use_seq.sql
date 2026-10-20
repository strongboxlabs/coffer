-- =============================================================================
-- 096 — Balance trigger walks (posted_at, seq) (ADR-0034 v2)
-- =============================================================================
--
-- Replaces fn_recompute_balances_for_account from mig 090. Same
-- semantics (header-walk, per-account aggregate, running-SUM into
-- txn_header_account_balances), but the ORDER BY no longer needs a
-- UUID tiebreaker — (h.posted_at, h.seq) is unique within an account
-- and globally monotonic.
--
-- One-shot re-backfill at the end: existing balance rows were
-- computed under the old (posted_at, created_at, id) ordering. With
-- batch-imported headers all sharing created_at, the prior walk
-- ordered by header.id (random UUID). The new walk orders by seq
-- (stable, matches the new register sort). Recompute every account
-- so balances align with the new ordering.
-- =============================================================================

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

    -- Anchor: balance after the last header strictly before the
    -- recompute window, ordered by canonical (posted_at, seq) DESC.
    SELECT thab.balance_after
      INTO v_starting
      FROM txn_header_account_balances thab
      JOIN txn_headers h ON h.id = thab.header_id
     WHERE thab.account_id = p_account_id
       AND h.posted_at < p_from_posted_at
     ORDER BY h.posted_at DESC, h.seq DESC
     LIMIT 1;

    IF v_starting IS NULL THEN
        SELECT a.opening_balance INTO v_starting FROM accounts a WHERE a.id = p_account_id;
    END IF;
    v_starting := COALESCE(v_starting, 0);

    -- Wipe the account's window.
    DELETE FROM txn_header_account_balances thab
     USING txn_headers h
     WHERE thab.header_id = h.id
       AND thab.account_id = p_account_id
       AND h.posted_at >= p_from_posted_at;

    -- Rebuild with header-walk + canonical (posted_at, seq) order.
    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after)
    WITH header_net AS (
        SELECT h.id AS header_id, h.posted_at, h.seq,
               SUM(l.amount) AS net_amount
          FROM txn_headers h
          JOIN txn_legs l ON l.header_id = h.id
         WHERE l.account_id = p_account_id
           AND h.is_merged_into IS NULL
           AND h.posted_at >= p_from_posted_at
         GROUP BY h.id, h.posted_at, h.seq
    )
    SELECT
        header_id,
        p_account_id,
        v_ledger_id,
        v_starting + SUM(net_amount) OVER (
            ORDER BY posted_at, seq
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        )
      FROM header_net;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION fn_recompute_balances_for_account(UUID, TIMESTAMPTZ) IS
    'ADR-0034 v2: header-walk recompute. Aggregates leg amounts per '
    'header for one account, running-sums in canonical (posted_at, '
    'seq) order.';

-- One-shot re-backfill so existing data switches to the new ordering.
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
