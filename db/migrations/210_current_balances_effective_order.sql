-- 210 — account_current_balances picks the latest row by EFFECTIVE date
--
-- The mig-133 view selects an account's current balance as the last
-- txn_header_account_balances row "ordered by RAW h.posted_at, h.seq", joining
-- txn_headers with no override join at all. But every writer of those balance_after
-- values accumulates them in OVERRIDE-AWARE order: mig 124 and mig 206 both walk
-- `ORDER BY COALESCE(o.posted_at, h.posted_at), seq`.
--
-- So when txn_header_overrides.posted_at (mig 093) moves a header past the account's
-- raw-latest header, "the last row by raw date" is no longer the last row of the
-- running sum. The view returns an INTERMEDIATE cumulative value — a real balance
-- from the middle of the sequence, which is why it looks plausible rather than
-- obviously broken.
--
-- This is not a new discovery. Mig 173 diagnosed exactly this bug in
-- account_balance_as_of and fixed it there, in its own words: "Bound + order by the
-- SAME COALESCE the recompute's running sum uses". The current as-of path still
-- honours that (mig 203). The mig-133 view was simply never given the same
-- treatment, so the dashboard overview, the brokerage-cash read in
-- HoldingsRepository, and the loan-payoff figure kept the raw ordering.
--
-- Fix is the mig-173 shape, verbatim minus the as-of bound this view does not have:
-- LEFT JOIN txn_header_overrides and order by COALESCE(o.posted_at, h.posted_at).
-- Everything else about the view is unchanged, including security_invoker.
-- ---------------------------------------------------------------------------

BEGIN;

CREATE OR REPLACE VIEW account_current_balances AS
SELECT
    a.id        AS account_id,
    a.ledger_id AS ledger_id,
    a.is_active AS is_active,
    COALESCE(latest.balance_after, a.opening_balance) AS balance
FROM accounts a
LEFT JOIN LATERAL (
    SELECT thab.balance_after
      FROM txn_header_account_balances thab
      JOIN txn_headers h ON h.id = thab.header_id
      -- The join mig 133 was missing. Without it the ORDER BY below reads a
      -- different sequence than the one the balances were accumulated in.
      LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     WHERE thab.account_id = a.id
     ORDER BY COALESCE(o.posted_at, h.posted_at) DESC, h.seq DESC
     LIMIT 1
) latest ON TRUE;

ALTER VIEW account_current_balances SET (security_invoker = true);

COMMENT ON VIEW account_current_balances IS
    'ADR-0056: the single definition of an account''s current balance — the latest '
    'txn_header_account_balances row, falling back to opening_balance. Ordered by the '
    'OVERRIDE-AWARE effective date (mig 210), because that is the order the recompute '
    'accumulates balance_after in; ordering by raw posted_at returned a mid-sequence '
    'value whenever an override moved a header.';

COMMIT;
