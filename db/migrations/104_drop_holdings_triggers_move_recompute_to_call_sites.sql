-- =============================================================================
-- 104 — Drop holdings recompute triggers; recompute moves to API call sites
-- =============================================================================
--
-- WHY (continued from ADR-0032 + mig 102)
--
-- Mig 102 retired the balance-trigger family in favour of an EF
-- SaveChangesInterceptor. The holdings/lots trigger family was
-- intentionally kept then — different recompute surface, narrower
-- trigger set, no observed bugs.
--
-- We're cleaning it up anyway. The same arguments that made balance
-- triggers a continuous source of bugs apply here in latent form:
--
--   * AFTER STATEMENT triggers with REFERENCING transition tables
--     see whatever the statement saw — they don't span an EF
--     SaveChanges batch. A multi-statement save (e.g. delete a
--     header → cascade legs → INSERT replacement legs) fires the
--     trigger per statement and re-derives state from interim
--     positions instead of the post-batch end state.
--   * The "leg moving between (account, security) pairs" case fires
--     the UPDATE-OLD trigger then UPDATE-NEW trigger; each
--     recomputes one side. Correct, but two SQL function calls
--     where the explicit-call pattern would do one DISTINCT pair
--     set + one batched recompute.
--   * The recompute is invisible to the writer. F12 on
--     InvestmentTransactionsRepository.PatchAsync doesn't lead a
--     reader to recompute_holdings_cost_basis.
--   * The trigger family duplicates the dispatch logic that the
--     interceptor pattern (mig 102) gets for free.
--
-- WHAT CHANGES
--
-- The four txn_legs holdings triggers go away, along with their
-- handler function. recompute_holdings_cost_basis stays (it's the
-- algorithm); a new TVF wrapper exposes it for HasDbFunction-bound
-- LINQ so the C# side can drive narrow recomputes via the same
-- pattern as recompute_balances_for_account (mig 102).
--
-- DROPPED
--
--   * trg_txn_legs_recompute_insert     (AFTER INSERT, NEW)
--   * trg_txn_legs_recompute_delete     (AFTER DELETE, OLD)
--   * trg_txn_legs_recompute_update_old (AFTER UPDATE, OLD)
--   * trg_txn_legs_recompute_update_new (AFTER UPDATE, NEW)
--   * trg_txn_legs_recompute_holdings() (handler)
--
-- KEPT
--
--   * recompute_holdings_cost_basis(UUID, UUID, UUID) — algorithm.
--   * recompute_holdings_for_brokerage(UUID) — mig 088 wrapper still
--     used by AccountsRepository.SetIsTradeCommissionAsync (commission
--     flip).
--
-- ADDED
--
--   * recompute_holdings_for_account_security(UUID, UUID) — TVF
--     wrapper returning (account_id UUID) so EF Core's HasDbFunction
--     can bind it. Calls recompute_holdings_cost_basis(NULL, account,
--     security) under the hood and returns the input account_id for
--     the LINQ projection.
--
-- NO ONE-SHOT REPAIR
--
-- Unlike mig 102 (which had stale balance rows from the
-- batch-fire-order bug) and mig 103 (which had inflated balances from
-- ignoring is_hidden), the holdings/lots state is correct as of the
-- last trigger fire — the trigger family never produced observed
-- bugs. Migrations run at API startup, before any new writes; the
-- interceptor takes over for the next save onward.
--
-- C# COMPANION
--
--   * RecomputeHoldingsForAccountSecurityRow + AppDbContext
--     HasDbFunction binding for the new wrapper.
--   * HoldingsRecomputeService: dedupes (account, security) pairs and
--     invokes the wrapper via LINQ.
--   * HoldingsRecomputeInterceptor: scans ChangeTracker for
--     TxnLegRow Added / Modified / Deleted entries with security_id
--     IS NOT NULL AND quantity IS NOT NULL; on Modified, captures
--     BOTH OLD and NEW (account, security) pairs so legs moving
--     between holdings reconcile both ends; on header-cascade DELETE,
--     reads doomed legs from the live DB before the cascade (same
--     pattern as BalanceRecomputeInterceptor).
--   * Registered in Program.cs alongside BalanceRecomputeInterceptor.
--
-- The importer (Dapper, no EF) already calls
-- InvestmentRepository.RecomputeCostBasisAsync(ledgerId) at end of
-- import as a final scrub — that path is unchanged.
--
-- AccountsRepository.SetIsTradeCommissionAsync is also unchanged; it
-- already invokes recompute_holdings_for_brokerage explicitly per
-- mig 088. The commission flip trigger was the first one we moved to
-- the explicit-call pattern.
-- =============================================================================

-- 1) Drop triggers.

DROP TRIGGER IF EXISTS trg_txn_legs_recompute_insert     ON txn_legs;
DROP TRIGGER IF EXISTS trg_txn_legs_recompute_delete     ON txn_legs;
DROP TRIGGER IF EXISTS trg_txn_legs_recompute_update_old ON txn_legs;
DROP TRIGGER IF EXISTS trg_txn_legs_recompute_update_new ON txn_legs;

-- 2) Drop trigger handler function. recompute_holdings_cost_basis
-- (the algorithm) is NOT dropped; it stays callable from C# and from
-- the mig-088 commission-flip wrapper.

DROP FUNCTION IF EXISTS trg_txn_legs_recompute_holdings();

-- 3) TVF wrapper for HasDbFunction binding. EF Core's HasDbFunction
-- works cleanly with TVFs returning a typed shape; void scalar
-- functions are awkward. Returning the input account_id gives the
-- LINQ side something to project; the caller discards it.

CREATE OR REPLACE FUNCTION recompute_holdings_for_account_security(
    p_account_id  UUID,
    p_security_id UUID
) RETURNS TABLE(account_id UUID)
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM recompute_holdings_cost_basis(NULL, p_account_id, p_security_id);
    RETURN QUERY SELECT p_account_id;
END;
$$;

COMMENT ON FUNCTION recompute_holdings_for_account_security(UUID, UUID) IS
    'TVF wrapper over recompute_holdings_cost_basis so EF Core can '
    'invoke the holdings recompute via HasDbFunction-bound LINQ. '
    'Caller discards the returned account_id; the side effect on '
    'holdings + lots is what matters. Parallels '
    'recompute_balances_for_account (mig 102). ADR-0032 (triggers as '
    'last resort), mig 104 (drop the txn_legs holdings triggers).';

GRANT EXECUTE ON FUNCTION recompute_holdings_for_account_security(UUID, UUID)
    TO coffer_app;
