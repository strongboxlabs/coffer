-- 173 — account_balance_as_of honors the posted_at override (align with the balance recompute)
--
-- The mig-172 account_balance_as_of picks the latest txn_header_account_balances
-- (thab) row with h.posted_at <= p_as_of, ordered by RAW h.posted_at. But
-- fn_recompute_balances_for_account (mig 124) computes those balance_after values
-- cumulatively in COALESCE(o.posted_at, h.posted_at), seq order -- the OVERRIDE-
-- aware effective date (a transaction whose date is edited writes
-- txn_header_overrides.posted_at, mig 093). So a date-overridden header was
-- bounded/ordered here by its raw posted_at while its balance_after reflected the
-- effective date: account_balance_as_of (hence net_worth_history + returns cash)
-- could count a header at an instant its effective date has not reached yet.
-- Bound + order by the SAME COALESCE the recompute's running sum uses.
--
-- Only the DATE needs aligning. An override-HIDDEN header has no thab row (the
-- recompute's COALESCE(o.is_hidden, h.is_hidden) filter drops it), so it is
-- already absent here; and thab.balance_after already reflects override amounts
-- (COALESCE(lo.amount, l.amount)). The holdings feeder
-- (holdings_market_value_as_of) intentionally stays RAW -- it mirrors
-- recompute_holdings_cost_basis, which is itself raw (hd.posted_at / hd.is_hidden,
-- no override join), so the as-of quantity equals the authoritative holdings. The
-- balance-vs-holdings override asymmetry is a pre-existing app property, not
-- introduced here (see docs/follow-ups.md).

CREATE OR REPLACE FUNCTION account_balance_as_of(
    p_ledger_id  UUID,
    p_as_of      TIMESTAMPTZ,
    p_account_id UUID DEFAULT NULL
)
RETURNS TABLE(account_id UUID, balance NUMERIC)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    SELECT a.id,
           COALESCE(latest.balance_after, a.opening_balance)
    FROM accounts a
    LEFT JOIN LATERAL (
        SELECT thab.balance_after
        FROM txn_header_account_balances thab
        JOIN txn_headers h ON h.id = thab.header_id
        LEFT JOIN txn_header_overrides o ON o.header_id = h.id
        WHERE thab.account_id = a.id
          AND COALESCE(o.posted_at, h.posted_at) <= p_as_of
        ORDER BY COALESCE(o.posted_at, h.posted_at) DESC, h.seq DESC
        LIMIT 1
    ) latest ON TRUE
    WHERE a.ledger_id = p_ledger_id
      AND (p_account_id IS NULL OR a.id = p_account_id);
$$;

GRANT EXECUTE ON FUNCTION account_balance_as_of(UUID, TIMESTAMPTZ, UUID) TO coffer_app;
