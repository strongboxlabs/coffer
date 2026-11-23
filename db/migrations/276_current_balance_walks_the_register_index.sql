-- =============================================================================
-- 276 — a current balance is found by walking the register's own index
-- =============================================================================
--
-- WHY
-- ---
-- account_balances_as_of_now (migration 259) is the cash side of every current value:
-- the sidebar's balances, the overview and the balance forecast all read it through
-- AccountBalancesRepository. For each account it looks up the register's latest running
-- balance before the boundary. The lookup named no ledger, so no index on txn_headers
-- could serve it: once per account, the planner read every header dated before the
-- boundary and sorted them by (posted_at, seq), then probed the running balances in that
-- order until one belonged to the account. The read repeated a scan and a sort of the
-- ledger's headers for every account it holds.
--
-- WHAT CHANGES
-- ------------
-- The lookup names the account's own ledger and keeps to the headers the register shows:
-- not hidden and not merged into another. Those are the conditions of
-- idx_txn_headers_ledger_posted_seq (migration 095), which holds a ledger's visible
-- headers in the register's own order, (posted_at DESC, seq DESC). Each account's lookup
-- walks that index back from the boundary and stops at the first header carrying a
-- running balance on the account. No index is added.
--
-- The row found is the same one:
--   * The ledger. A running balance's header and account are in one ledger
--     (txn_header_account_balances' composite foreign keys, migration 089), so naming
--     the account's ledger leaves out no row.
--   * Hidden and merged headers. The balance walk (balance_walk) stores no running
--     balance on either, and an account's recompute (fn_recompute_balances_for_account)
--     deletes every stored row in its window before writing the walk's (on any header
--     since migration 277), so a header hidden or merged away loses its own. No header
--     carrying a running balance is left out. A row stored on such a header anyway is one
--     no write path leaves: this read ignores it, as the register does, and the balances
--     check reports it as a stored row the walk does not produce, which the balances
--     repair removes.
--   * The order. Still (posted_at DESC, seq DESC), which the index holds exactly, so a
--     tie on a day is broken as before. The other index on (ledger_id, posted_at), from
--     migration 022, breaks ties by id: an incremental sort would have to reorder each
--     day by seq and read on past the first match to the account's next row on an
--     earlier day, the whole ledger for an account with one row.
--
-- The signature, return type, language, volatility and parallel safety are unchanged, so
-- CREATE OR REPLACE keeps the function's owner and grants, and it stays SECURITY
-- INVOKER: row-level security applies as the caller, as before. The
-- account_current_balances view reads it and needs no change. The COMMENT is re-issued.
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
       COALESCE(latest.balance_after, a.opening_balance) AS balance
  FROM accounts a
  LEFT JOIN LATERAL (
      SELECT thab.balance_after
        FROM txn_headers h
        JOIN txn_header_account_balances thab
          ON thab.header_id = h.id
         AND thab.account_id = a.id
       -- idx_txn_headers_ledger_posted_seq's conditions, then the register's Scheduled
       -- test, negated: a row at or after the boundary is Scheduled there and does not
       -- count here.
       WHERE h.ledger_id = a.ledger_id
         AND NOT h.is_hidden
         AND h.is_merged_into IS NULL
         AND h.posted_at < p_scheduled_from
       ORDER BY h.posted_at DESC, h.seq DESC
       LIMIT 1) latest ON true
 WHERE p_ledger_id IS NULL OR a.ledger_id = p_ledger_id;
$function$;

COMMENT ON FUNCTION account_balances_as_of_now(uuid, timestamp with time zone) IS
    'ADR-0056 (mig 259): the single definition of an account''s current balance. The '
    'latest txn_header_account_balances row dated before p_scheduled_from, in '
    '(posted_at, seq) order, falling back to opening_balance. p_scheduled_from is the '
    'register''s Scheduled boundary (the caller''s local tomorrow at UTC midnight), so '
    'a row counts exactly when the register stops calling it Scheduled. NULL ledger '
    'means every visible account. Since mig 276 each account''s lookup walks '
    'idx_txn_headers_ledger_posted_seq back from the boundary (its ledger''s visible '
    'headers; hidden and merged headers carry no running balance).';

COMMIT;
