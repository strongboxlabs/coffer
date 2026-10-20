-- =============================================================================
-- 098 — txn_header_account_balances.net_amount (ADR-0034 v2 follow-up)
-- =============================================================================
--
-- Mirrors `balance_after` (cumulative) with its per-step counterpart
-- (delta). Same grain — per (header, account) — so storing both is
-- the natural shape. The recompute function already computes
-- net_amount inside the `header_net` CTE; this migration adds the
-- column, drops it into the INSERT, and re-backfills.
--
-- Why store it instead of computing on every read:
--   * resolved_transactions today projects per-leg amount (the
--     `amount` column = COALESCE(lo.amount, l.amount)). The SPA's
--     `groupAmount(legs)` then sums those at render time for the
--     "Amount" slot on investment / multi-split rows.
--   * Net amount per (header, account) is a stable fact computed
--     once at write time. Storing it removes the read-side
--     aggregation, makes "the amount on this register row" a
--     direct fetch, and mirrors `balance_after`'s shape.
--
-- Per-leg `amount` stays on the view (the split editor needs it).
-- The new column surfaces alongside balance_after for any consumer
-- that wants the group total directly (mig 100 projects it on the
-- view as `header_account_net_amount`).
-- =============================================================================

-- 1) Add the column (nullable so the backfill DO block can populate
-- it before we lock NOT NULL).
ALTER TABLE txn_header_account_balances ADD COLUMN net_amount NUMERIC(19, 4);

-- 2) Update the recompute function to populate net_amount alongside
-- balance_after. Same CTE shape — we just project net_amount through.
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

    DELETE FROM txn_header_account_balances thab
     USING txn_headers h
     WHERE thab.header_id = h.id
       AND thab.account_id = p_account_id
       AND h.posted_at >= p_from_posted_at;

    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after, net_amount)
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
        ) AS balance_after,
        net_amount
      FROM header_net;
END;
$$ LANGUAGE plpgsql;

-- 3) Re-backfill every account so existing rows pick up net_amount.
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

-- 4) Lock NOT NULL now that every row has a value.
ALTER TABLE txn_header_account_balances ALTER COLUMN net_amount SET NOT NULL;

COMMENT ON COLUMN txn_header_account_balances.net_amount IS
    'Net cash effect of this header on this account (sum of leg amounts '
    'where account_id matches). The per-step delta that balance_after '
    'accumulates over canonical (posted_at, seq) order.';
