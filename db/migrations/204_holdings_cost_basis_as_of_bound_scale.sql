-- 204 — bound the scale of holdings_cost_basis_as_of so it fits System.Decimal
--
-- THE BUG. holdings_snapshot threw for every account whose cost basis carried a
-- long fractional scale:
--
--   System.OverflowException: Numeric value does not fit in a System.Decimal
--     at Npgsql...PgNumeric.Builder.ToDecimal
--     at InvestmentReportingRepository.HoldingsSnapshotAsync
--
-- holdings_cost_basis_as_of (mig 202) returned the walk's o_quantity and
-- o_cost_basis RAW. Postgres NUMERIC is arbitrary-precision; System.Decimal is not
-- — it holds 28-29 significant digits and Npgsql throws rather than truncate.
--
-- Scale gets in through the sell branch. A buy adds its leg AMOUNT, which is exact,
-- but a sell subtracts take x unit_cost where unit_cost = amount / quantity — a
-- division that rarely terminates. One partial sale of a real position is enough:
-- observed 240755691.470000775766666666665996, which is 34 digits.
--
-- Whether it threw therefore depended on MAGNITUDE, which is why it looked
-- data-random in production: Maryland529 (basis 34,585.5700, divides evenly) was
-- fine, Fidelity GDIT (196,256.69839381536152097920000, 28 digits) squeaked under,
-- and a $2.98M account went over. The whole-ledger call failed because any one
-- account failing fails the read.
--
-- THE FIX, and where it should have been from the start. Round at the boundary the
-- client reads, exactly as the sibling feeders already do — mig 172 and mig 200 both
-- ROUND their outputs and say why: "Bound scales so the NUMERICs fit System.Decimal".
-- The writer never hit this because holdings.cost_basis is NUMERIC(19,4) and the
-- column rounds on INSERT; only a function RETURNING raw values was exposed. That is
-- the "unconstrained NUMERIC in RETURNS TABLE" blind spot recorded in closed-work.md
-- — documented, then reintroduced by the migration that added this function.
--
-- Money is 4dp (matching holdings.cost_basis NUMERIC(19,4)); shares are 12dp
-- (matching NUMERIC(25,12) and mig 200's quantity rounding).

CREATE OR REPLACE FUNCTION holdings_cost_basis_as_of(
    p_ledger_id   UUID,
    p_as_of       TIMESTAMPTZ,
    p_account_ids UUID[] DEFAULT NULL
)
RETURNS TABLE(account_id UUID, security_id UUID, quantity NUMERIC, cost_basis NUMERIC)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    -- Positions are discovered from the LEGS, not from the holdings projection: a
    -- position closed since p_as_of has no projection row but was held then. The
    -- qty <> 0 filter drops anything closed BY p_as_of, matching the valuation feeder.
    SELECT p.account_id,
           p.security_id,
           ROUND(w.o_quantity, 12)  AS quantity,
           ROUND(w.o_cost_basis, 4) AS cost_basis
    FROM (
        SELECT DISTINCT l.account_id, l.security_id
        FROM txn_legs l
        JOIN live_txn_headers h ON h.id = l.header_id
        WHERE l.ledger_id     = p_ledger_id
          AND l.security_id  IS NOT NULL
          AND l.quantity     IS NOT NULL
          AND h.is_hidden     = FALSE
          AND h.posted_at    <= p_as_of
          AND (p_account_ids IS NULL OR l.account_id = ANY (p_account_ids))
    ) p
    CROSS JOIN LATERAL holdings_fifo_walk(p.account_id, p.security_id, p_as_of) w
    WHERE w.o_quantity <> 0;
$$;

COMMENT ON FUNCTION holdings_cost_basis_as_of(UUID, TIMESTAMPTZ, UUID[]) IS
    'FIFO cost basis + quantity per (holdings-account, security) as of an instant '
    '(mig 202), via the same holdings_fifo_walk the writer persists. Scales are '
    'bounded (mig 204) so the NUMERICs fit System.Decimal: money 4dp, shares 12dp. '
    'Returning them raw overflowed Npgsql for any account whose basis carried a long '
    'fractional scale from a partial sale.';
