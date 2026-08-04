-- =============================================================================
-- 133 — account_current_balances view (ADR-0056 slice 1)
-- =============================================================================
--
-- The single definition of "an account's current balance", so the dashboard
-- overview, HoldingsRepository's brokerage-cash read, and any future consumer
-- share ONE source of truth instead of each re-deriving it.
--
-- Balance = the register's own latest balance_after for the account (the last
-- header by canonical (posted_at, seq) order, from txn_header_account_balances
-- per ADR-0034), falling back to opening_balance for an account with no
-- transactions yet. This is the exact value the register shows — not a parallel
-- re-sum — read set-based (one LATERAL per account) so a caller can fetch every
-- account's balance in a single query rather than N round-trips.
--
-- security_invoker so RLS on the underlying accounts / txn_header_account_balances
-- applies as the querying role (matches resolved_transactions).
-- =============================================================================

CREATE VIEW account_current_balances AS
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
     WHERE thab.account_id = a.id
     ORDER BY h.posted_at DESC, h.seq DESC
     LIMIT 1
) latest ON TRUE;

ALTER VIEW account_current_balances SET (security_invoker = true);
