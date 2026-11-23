-- =============================================================================
-- 279 — a current balance costs no more than the account's own running balances
-- =============================================================================
--
-- WHY
-- ---
-- account_balances_as_of_now is the cash side of every current value: the sidebar's
-- balances, the overview and the balance forecast all read it through
-- AccountBalancesRepository. For each account it takes the running balance on the latest
-- header before the boundary that is in the account's ledger and is neither hidden nor
-- merged away (migration 276). Since 276 the planner finds that header by walking the
-- ledger's visible headers back from the boundary (idx_txn_headers_ledger_posted_seq)
-- and probing the running balances at each one until it finds one on the account, with
-- nothing to stop the walk sooner. The walk is short for an account with a row shortly
-- before the boundary. For an account with no running balance (a category never used,
-- an account holding only its opening balance, a brokerage never traded in) it reads
-- every header of the ledger dated before the boundary, and for one whose last row is
-- years back, nearly every one. A ledger gathers such accounts as it ages, so the read
-- cost the ledger's header count once for each of them.
--
-- Reading each account's own running balances instead, and taking the latest, costs the
-- account's own row count: nothing for a quiet account, but every row it has ever had for
-- a busy one, where the walk would have stopped after a few headers. Neither lookup alone
-- is bounded by what the account holds and also short for a busy account.
--
-- WHAT CHANGES
-- ------------
-- Each account's lookup takes three steps, each bounded by the account's own running
-- balances:
--
--   1. It counts the account's running balances (idx_thab_account_ledger): n.
--   2. It walks the register's index (idx_txn_headers_ledger_posted_seq) back from the
--      boundary over at most n of the ledger's visible headers, probes each one's running
--      balance on the account by its primary key, and stops at the first it finds. For
--      an account with no running balance n is 0 and nothing is walked.
--   3. Only when the walk finds none, it reads the account's own running balances
--      (idx_thab_account_ledger), reaches each one's header by its key, and takes the
--      latest the rules below admit.
--
-- The bound is the account's own row count, not a tuned number: the walk reads at most as
-- many headers as the account has running balances, and the count and the fallback read
-- those rows once each, so the lookup costs a small multiple of the account's row count,
-- whatever else the ledger holds, and stays a few headers for a busy account. The bound
-- decides only how far the walk goes before it hands over, never the answer. The walk
-- applies the rules below in the register's order, so the first header it finds is the
-- one 276 finds. When it finds none within n headers, the fallback applies the same rules
-- to every running balance on the account, exactly. The row found is therefore 276's on
-- every state, drifted ones included: a running balance on a hidden or merged-away
-- header, or on one at or after the boundary, counts towards n but is never taken.
--
-- What keeps the plan to those steps: the walk's LIMIT keeps it a subquery the planner
-- cannot flatten; the running balance it probes, and the header the fallback probes, are
-- each read in a subquery of their own (OFFSET 0), once per row and by key, so neither
-- step can be turned into a join driven from the other side; and the fallback's gate (the
-- walk found none) names only the walk's result, so it is tested once per account before
-- the fallback reads any row. No index is added.
--
-- The rules are 276's, unchanged, so the row found is the same on every state, one where
-- stored balances have drifted included:
--   * the header is in the account's ledger;
--   * it is neither hidden nor merged into another, so a running balance stored on such
--     a header is skipped, as the register skips it;
--   * it is dated before the boundary: the register's Scheduled test, negated;
--   * the latest is the first in (posted_at DESC, seq DESC), the register's own order, so
--     a tie on a day is broken as before.
--
-- The signature, return type, language, volatility and parallel safety are unchanged, so
-- CREATE OR REPLACE keeps the function's owner and grants, and it stays SECURITY
-- INVOKER: row-level security applies as the caller. The account_current_balances view
-- reads it and needs no change. The COMMENT is re-issued.
--
-- EXISTING INSTALLS
-- -----------------
-- No row is read or written; only the function is redefined.
-- =============================================================================

BEGIN;

CREATE OR REPLACE FUNCTION public.account_balances_as_of_now(p_ledger_id uuid, p_scheduled_from timestamp with time zone)
 RETURNS TABLE(account_id uuid, ledger_id uuid, is_active boolean, balance numeric)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
SELECT a.id AS account_id,
       a.ledger_id,
       a.is_active,
       COALESCE(near.balance_after, own.balance_after, a.opening_balance) AS balance
  FROM accounts a
  -- 1. How many running balances the account holds (idx_thab_account_ledger): the most
  --    headers the walk reads.
  CROSS JOIN LATERAL (
      SELECT count(*) AS n
        FROM txn_header_account_balances c
       WHERE c.account_id = a.id) held
  -- 2. The register's latest headers before the boundary, newest first
  --    (idx_txn_headers_ledger_posted_seq), no more of them than the account holds
  --    running balances; the first that carries one on the account. The LIMIT keeps the
  --    walk a subquery of its own, and OFFSET 0 keeps the probe of each header's running
  --    balance one by key, so the walk stops at the first it finds.
  LEFT JOIN LATERAL (
      SELECT b.balance_after
        FROM (SELECT h.id, h.posted_at, h.seq
                FROM txn_headers h
               -- 276's rules: the account's ledger, a header neither hidden nor merged
               -- away, then the register's Scheduled test, negated: a row at or after
               -- the boundary is Scheduled there and does not count here.
               WHERE h.ledger_id = a.ledger_id
                 AND NOT h.is_hidden
                 AND h.is_merged_into IS NULL
                 AND h.posted_at < p_scheduled_from
               ORDER BY h.posted_at DESC, h.seq DESC
               LIMIT held.n) w
        CROSS JOIN LATERAL (
            SELECT thab.balance_after
              FROM txn_header_account_balances thab
             WHERE thab.header_id = w.id
               AND thab.account_id = a.id
            OFFSET 0) b
       ORDER BY w.posted_at DESC, w.seq DESC
       LIMIT 1) near ON true
  -- 3. Only when the walk found none: the account's own running balances
  --    (idx_thab_account_ledger), each one's header by key. OFFSET 0 keeps the header a
  --    subquery of its own, read once per running balance, so the planner cannot drive
  --    this step from the headers. The gate names only the walk's result, so it is
  --    tested once per account before any row is read.
  LEFT JOIN LATERAL (
      SELECT thab.balance_after
        FROM txn_header_account_balances thab
        CROSS JOIN LATERAL (
            SELECT h.posted_at, h.seq
              FROM txn_headers h
             WHERE h.id = thab.header_id
               -- 276's rules, as in the walk.
               AND h.ledger_id = a.ledger_id
               AND NOT h.is_hidden
               AND h.is_merged_into IS NULL
               AND h.posted_at < p_scheduled_from
            OFFSET 0) h
       WHERE thab.account_id = a.id
         AND near.balance_after IS NULL
       ORDER BY h.posted_at DESC, h.seq DESC
       LIMIT 1) own ON true
 WHERE p_ledger_id IS NULL OR a.ledger_id = p_ledger_id;
$function$;

COMMENT ON FUNCTION account_balances_as_of_now(uuid, timestamp with time zone) IS
    'ADR-0056 (mig 259): the single definition of an account''s current balance. The '
    'latest txn_header_account_balances row dated before p_scheduled_from, in '
    '(posted_at, seq) order, falling back to opening_balance. p_scheduled_from is the '
    'register''s Scheduled boundary (the caller''s local tomorrow at UTC midnight), so '
    'a row counts exactly when the register stops calling it Scheduled. NULL ledger '
    'means every visible account. Since mig 276 only a header in the account''s ledger '
    'that is neither hidden nor merged away counts (neither carries a running balance '
    'the walk writes). Since mig 279 each account''s lookup counts its own running '
    'balances (idx_thab_account_ledger), walks idx_txn_headers_ledger_posted_seq back '
    'from the boundary over at most that many headers, and only when the walk finds '
    'none reads the account''s own running balances and their headers by key, so it '
    'costs at most a small multiple of the account''s row count, never the ledger''s '
    'header count, and the row found is the one the rules name on every state.';

COMMIT;
