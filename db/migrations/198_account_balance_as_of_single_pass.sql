-- =============================================================================
-- 198 — account_balance_as_of: one pass over headers, not one pass per account
-- =============================================================================
--
-- The function answers "what was every account's balance at instant T". It did
-- that with a LATERAL subquery per account, and each iteration re-derived the
-- effective posted date by joining txn_headers to txn_header_overrides. The
-- planner served that join by sequentially scanning the WHOLE headers table,
-- once per account:
--
--   Nested Loop Left Join
--     ->  Seq Scan on accounts a
--     ->  Limit                                      (once per account)
--           ->  Sort  (top-N heapsort)
--                 ->  Hash Join
--                       ->  Seq Scan on txn_headers  (every header, every loop)
--
-- Every header is visited once per account to return one number per account.
-- The cost is driven by the number of ACCOUNTS in the ledger multiplied by the
-- size of the whole transaction table, neither of which has anything to do with
-- how much work the answer needs.
--
-- WHY IT MATTERS: time-weighted return values the portfolio once per
-- external-flow instant, so a report over a ledger with N flow dates calls this
-- N times, and the per-call cost above makes the report quadratic. A cap on N
-- (MaxReturnsBoundaries, 400) bounds the cost without removing it, and raising
-- the cap lets a read request run for minutes; the fix is the per-call cost.
--
-- The rewrite scopes accounts first, joins the balance rows for those accounts
-- once, and picks each account's latest row with DISTINCT ON. Headers are
-- touched a single time regardless of account count.
--
-- Semantics are unchanged and deliberately so — this function backs every
-- balance the product shows, not just returns:
--   * same effective-date rule, COALESCE(override.posted_at, header.posted_at);
--   * same tie-break, effective date DESC then header seq DESC;
--   * same fallback to accounts.opening_balance when no row qualifies;
--   * same p_account_id filter, and same behaviour for an account with no
--     transactions at all.
-- The original filtered balance rows only by account (thab.account_id = a.id);
-- joining them to the already ledger-scoped account set is the same restriction
-- expressed once instead of per row.
-- =============================================================================

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
    WITH scoped AS (
        SELECT a.id, a.opening_balance
          FROM accounts a
         WHERE a.ledger_id = p_ledger_id
           AND (p_account_id IS NULL OR a.id = p_account_id)
    ),
    latest AS (
        SELECT DISTINCT ON (thab.account_id)
               thab.account_id AS acct,
               thab.balance_after
          FROM txn_header_account_balances thab
          JOIN scoped s          ON s.id = thab.account_id
          JOIN txn_headers h     ON h.id = thab.header_id
          LEFT JOIN txn_header_overrides o ON o.header_id = h.id
         WHERE COALESCE(o.posted_at, h.posted_at) <= p_as_of
         ORDER BY thab.account_id,
                  COALESCE(o.posted_at, h.posted_at) DESC,
                  h.seq DESC
    )
    SELECT s.id, COALESCE(l.balance_after, s.opening_balance)
      FROM scoped s
      LEFT JOIN latest l ON l.acct = s.id;
$$;

GRANT EXECUTE ON FUNCTION account_balance_as_of(UUID, TIMESTAMPTZ, UUID) TO coffer_app;
