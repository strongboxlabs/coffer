-- =============================================================================
-- 151 — transfer_shares action (ADR-0065)
-- =============================================================================
--
-- Adds the in-kind share-transfer action to the catalog (ADR-0027 set, last set
-- in migration 062). A transfer_shares moves shares between two holdings accounts
-- with NO realized gain and the source cost basis CARRIED (per-lot) to the
-- destination — distinct from sell+buy (which fabricates a gain + resets basis).
--
-- This migration only widens the CHECK. The posting model + the per-lot-carry
-- recompute handling land with the feature code (recompute_holdings_cost_basis is
-- updated in a later migration in this slice).
-- =============================================================================

ALTER TABLE txn_headers DROP CONSTRAINT txn_headers_action_check;
ALTER TABLE txn_headers
    ADD CONSTRAINT txn_headers_action_check
    CHECK (action IS NULL OR action IN (
        'buy', 'buyx',
        'sell', 'sellx',
        'dividend_cash', 'dividend_reinvest', 'divx',
        'transfer', 'transfer_shares',
        'misc'
    ));
