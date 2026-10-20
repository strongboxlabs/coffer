-- =============================================================================
-- 088 — drop trg_accounts_recompute_on_commission_flip; recompute moves to API
-- =============================================================================
--
-- Final removal in the validation-trigger sweep under
-- [ADR-0032](../decisions/0032-triggers-as-last-resort.md), via the
-- escape hatch the ADR's own three-gate test allows: API code
-- carries the side-effect through HasDbFunction binding (the
-- documented mechanism for "complex SQL lives in migrations, called
-- from LINQ" — engineering-standards §4.2.1).
--
-- AUDIT (corrected from the earlier ADR §3 table):
--
--   * AccountsRepository.SetIsTradeCommissionAsync is the sole writer
--     that flips accounts.is_trade_commission.
--   * The old trigger trg_accounts_recompute_on_commission_flip fired
--     AFTER UPDATE OF that column and PERFORMed recompute_holdings_
--     cost_basis(NULL, NEW.holdings_account_id, NULL).
--   * Moving the recompute to the API call site makes the data flow
--     explicit: the endpoint that flips the flag also requests the
--     recompute, in the same transaction the endpoint already opens.
--
-- WRAPPER FUNCTION
--
-- recompute_holdings_cost_basis returns void. EF Core's HasDbFunction
-- prefers a value-returning function so the LINQ stub has a typeable
-- return shape. recompute_holdings_for_brokerage is a thin scalar-
-- returning wrapper: it PERFORMs the void function and then returns
-- the count of holdings rows under the brokerage (useful for tests +
-- diagnostics; callers typically discard the value).
--
-- TVF-style return (RETURNS TABLE) so the EF binding matches the
-- established pattern from insert_investment_legs (migration 069) —
-- a keyless row type with a single named column, queried via LINQ
-- as IQueryable<RecomputeHoldingsForBrokerageRow>.
-- =============================================================================

CREATE OR REPLACE FUNCTION recompute_holdings_for_brokerage(
    p_holdings_account_id UUID
) RETURNS TABLE(recomputed_count INTEGER)
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM recompute_holdings_cost_basis(NULL, p_holdings_account_id, NULL);
    RETURN QUERY
        SELECT COUNT(*)::INTEGER FROM holdings WHERE account_id = p_holdings_account_id;
END;
$$;

COMMENT ON FUNCTION recompute_holdings_for_brokerage(UUID) IS
    'Thin scalar wrapper over recompute_holdings_cost_basis (void). '
    'Bound via HasDbFunction in AppDbContext so the API path '
    '(AccountsRepository.SetIsTradeCommissionAsync) can invoke the '
    'recompute via LINQ. Replaces trg_accounts_recompute_on_commission_flip '
    'per ADR-0032.';

DROP TRIGGER IF EXISTS trg_accounts_recompute_on_commission_flip ON accounts;
DROP FUNCTION IF EXISTS trg_accounts_is_trade_commission_recompute();
