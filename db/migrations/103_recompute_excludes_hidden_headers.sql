-- =============================================================================
-- 103 — fn_recompute_balances_for_account excludes is_hidden headers
-- =============================================================================
--
-- WHY
--
-- Soft-hiding a transaction (via the "Delete" action on a feed-imported
-- row, the bulk-delete soft-hide branch, or a future per-row "Hide"
-- override on the registers) removes it from view in the register
-- (RegisterRepository filters `!rt.IsHidden`) — but the recompute
-- function did NOT filter is_hidden, so the hidden row's amount kept
-- contributing to every downstream row's running balance. From the
-- user's perspective: "I deleted that transaction; why is the balance
-- still inflated?"
--
-- Two layers carry the hidden state:
--   * txn_headers.is_hidden          (raw header column)
--   * txn_header_overrides.is_hidden (per-header override, BOOLEAN
--     NULLABLE; NULL means "no override of this column")
--
-- The resolved view's effective expression (mig 100) is:
--   COALESCE(o.is_hidden, h.is_hidden, FALSE)
-- and the recompute must use the same predicate so the visible state
-- and the balance walk agree everywhere.
--
-- WHAT CHANGES
--
-- fn_recompute_balances_for_account adds the is_hidden filter to its
-- header_net CTE WHERE clause, matching the resolved view's COALESCE
-- chain. No other change to the function body.
--
-- WHY NOT ALSO FILTER THE v_starting QUERY: v_starting selects from
-- txn_header_account_balances, not from txn_headers directly. After
-- this migration, hidden headers don't have rows in
-- txn_header_account_balances (the DELETE step removes them and the
-- INSERT step skips them), so v_starting naturally walks past them
-- without an explicit filter.
--
-- ONE-SHOT REPAIR
--
-- Every account with at least one currently-hidden header has a stale
-- balance walk (the hidden row's amount is baked into every downstream
-- balance_after). Walk every account and re-derive from earliest time
-- — same shape as mig 099 / 101 / 102's one-shot loops.
--
-- INTERCEPTOR CHANGES (companion)
--
-- BalanceRecomputeInterceptor now treats is_hidden flips on either
-- txn_headers or txn_header_overrides as balance-affecting (previously
-- only posted_at / is_merged_into / leg amount / override amount).
--
-- BulkTransactionsRepository.BulkDeleteAsync's soft-hide branch uses
-- ExecuteUpdateAsync (bypasses ChangeTracker), so it now also captures
-- affected (account_id, posted_at) pairs and invokes
-- BalanceRecomputeService explicitly — same #4 pattern as the
-- hard-delete branch.
--
-- ADR-0034 amendment notes is_hidden in the canonical recompute
-- predicate set.
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
               SUM(COALESCE(lo.amount, l.amount)) AS net_amount
          FROM txn_headers h
          JOIN txn_legs l ON l.header_id = h.id
          LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
          LEFT JOIN txn_header_overrides o ON o.header_id = h.id
         WHERE l.account_id = p_account_id
           AND h.is_merged_into IS NULL
           -- Mig 103: hidden headers are excluded from the balance
           -- walk, matching the resolved view's effective-hidden
           -- predicate (COALESCE(o.is_hidden, h.is_hidden, FALSE)).
           -- Without this filter, soft-deleting a row inflated every
           -- downstream balance — the row disappeared from view but
           -- its amount kept contributing.
           AND COALESCE(o.is_hidden, h.is_hidden, FALSE) = FALSE
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

-- One-shot recompute: any account with at least one currently-hidden
-- header carries an inflated balance walk. Walk every account.
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
