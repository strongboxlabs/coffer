-- =============================================================================
-- 277 — a balance recompute reads and leaves only the running balances the walk writes
-- =============================================================================
--
-- WHY
-- ---
-- fn_recompute_balances_for_account replaces an account's running balances from an
-- anchor with what balance_walk derives, starting from a seed: the last stored running
-- balance before the anchor. Both ends read headers the walk does not count:
--
--   * It cleared the window through live_txn_headers, which leaves out recurring
--     templates, so a stored running balance on a template header survived every
--     recompute, the balances repair included.
--   * It read the seed through live_txn_headers too, which keeps hidden and merged-away
--     headers. A running balance stored on one of them, dated before a save's anchor,
--     could seed that save's recompute, and every balance the save wrote would carry its
--     figure.
--
-- The walk writes no running balance on a template, a hidden or a merged-away header, so
-- such a row is one no write path produces; but once stored, nothing removed it, and it
-- could seed.
--
-- WHAT CHANGES
-- ------------
-- One definition of the headers the walk counts, which the walk and the recompute's seed
-- both read:
--
--   * balance_walk_headers, a view: the headers that are not recurring templates
--     (live_txn_headers), not hidden and not merged into another. It holds the columns
--     the walk and the seed read.
--   * balance_walk reads its headers from it, in place of live_txn_headers and the two
--     conditions it applied inline. Its result is unchanged: the same headers, the same
--     rows, the same order.
--   * fn_recompute_balances_for_account seeds from the last stored running balance before
--     the anchor on a header balance_walk_headers holds that has a line on the account:
--     the (header, account) pairs the walk writes. A row on any other header cannot seed.
--   * It clears the window through txn_headers, every header of the account's rows dated
--     at or after the anchor, before the walk's rows are written. After a recompute, the
--     account's running balances in the window are exactly the walk's, whatever kind of
--     header a stray row sits on.
--
-- The write lock, the walk's arithmetic and the insert are unchanged. The two functions
-- keep their signatures, return types, languages and volatility, so CREATE OR REPLACE
-- keeps their owners and grants. The view reads as its caller (security_invoker) and
-- coffer_app may only read it, as for every view. The COMMENTs are re-issued.
--
-- EXISTING INSTALLS
-- -----------------
-- No row is read or written by the migration. A running balance an earlier version left on
-- a header the walk does not count is no longer read as a seed, and is removed by the
-- next recompute whose window covers it; the balances check reports it until then, and
-- the balances repair, which recomputes every account from the start, removes it.
-- =============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. The headers the walk counts.
-- ---------------------------------------------------------------------------
CREATE VIEW balance_walk_headers AS
 SELECT h.id,
        h.ledger_id,
        h.posted_at,
        h.seq
   FROM live_txn_headers h
  WHERE h.is_merged_into IS NULL
    AND COALESCE(h.is_hidden, FALSE) = FALSE;

ALTER VIEW balance_walk_headers SET (security_invoker = true);

GRANT SELECT ON balance_walk_headers TO coffer_app;

COMMENT ON VIEW balance_walk_headers IS
    'The headers the balance walk counts (mig 277): not a recurring template '
    '(live_txn_headers), not hidden, not merged into another. balance_walk reads its '
    'headers from it, and fn_recompute_balances_for_account its seed, so the two cannot '
    'disagree about which headers carry a running balance.';

-- ---------------------------------------------------------------------------
-- 2. The walk reads them.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.balance_walk(p_ledger_id uuid, p_account_id uuid, p_from_posted_at timestamp with time zone, p_starting_balance numeric)
 RETURNS TABLE(account_id uuid, header_id uuid, posted_at timestamp with time zone, seq bigint, net_amount numeric, balance_after numeric)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
    WITH header_net AS (
        SELECT l.account_id,
               -- Carried through the GROUP BY rather than joined again in the outer
               -- query: it is functionally dependent on account_id, and one join to
               -- accounts serves both the ledger filter and the per-account seed.
               COALESCE(a.opening_balance, 0)      AS opening_balance,
               h.id                               AS header_id,
               h.posted_at                        AS posted_at,
               h.seq                              AS seq,
               SUM(l.amount)                      AS net_amount
          -- The headers the walk counts (mig 277): not a template, not hidden, not
          -- merged into another.
          FROM balance_walk_headers h
          JOIN txn_legs l                   ON l.header_id = h.id
          JOIN accounts a                   ON a.id = l.account_id
         WHERE (p_account_id IS NULL OR l.account_id = p_account_id)
           -- Scoped through accounts, the way mig 188 scoped it, rather than through
           -- txn_legs.ledger_id. The two agree on well-formed data; the account's own
           -- ledger is the authority on which ledger a balance belongs to.
           AND (p_ledger_id IS NULL OR a.ledger_id = p_ledger_id)
           AND h.posted_at >= p_from_posted_at
         GROUP BY l.account_id, COALESCE(a.opening_balance, 0), h.id,
                  h.posted_at, h.seq
    )
    SELECT hn.account_id,
           hn.header_id,
           hn.posted_at,
           hn.seq,
           hn.net_amount,
           -- A given seed wins; NULL means "seed each account from its own opening
           -- balance", which is the whole-ledger-from-the-floor case. NULL rather than
           -- a sentinel because 0 is a legitimate explicit seed.
           COALESCE(p_starting_balance, hn.opening_balance)
               + SUM(hn.net_amount) OVER (
                   PARTITION BY hn.account_id
                   ORDER BY hn.posted_at, hn.seq
                   ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
               ) AS balance_after
      FROM header_net hn;
$function$;

-- ---------------------------------------------------------------------------
-- 3. The recompute seeds from them and leaves only what the walk writes.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.fn_recompute_balances_for_account(p_account_id uuid, p_from_posted_at timestamp with time zone)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_starting  NUMERIC(19, 4);
    v_ledger_id UUID;
BEGIN
    SELECT a.ledger_id INTO v_ledger_id FROM accounts a WHERE a.id = p_account_id;
    IF v_ledger_id IS NULL THEN
        RETURN;
    END IF;

    -- The ledger write lock (mig 275), before anything written from is read: nothing for a
    -- writer that took it first, this statement's for a statement of its own, refused in a
    -- transaction that wrote without it. A writer in flight is waited for, and everything
    -- below reads what it committed.
    PERFORM 1 FROM fn_ledger_write_lock(v_ledger_id);

    -- Seed: the last stored balance strictly before the anchor that the walk writes, on a
    -- header it counts with a line on the account (mig 277), else the account's opening
    -- balance. A row stored on any other header is not the walk's, and never seeds.
    SELECT thab.balance_after
      INTO v_starting
      FROM txn_header_account_balances thab
      JOIN balance_walk_headers h ON h.id = thab.header_id
     WHERE thab.account_id = p_account_id
       AND h.posted_at < p_from_posted_at
       AND EXISTS (SELECT 1 FROM txn_legs l
                    WHERE l.header_id = h.id AND l.account_id = p_account_id)
     ORDER BY h.posted_at DESC, h.seq DESC
     LIMIT 1;

    IF v_starting IS NULL THEN
        SELECT a.opening_balance INTO v_starting FROM accounts a WHERE a.id = p_account_id;
    END IF;
    v_starting := COALESCE(v_starting, 0);

    -- Every stored row in the window, on any header (mig 277): what remains after the
    -- insert below is exactly what the walk writes.
    DELETE FROM txn_header_account_balances thab
     USING txn_headers h
     WHERE thab.header_id = h.id
       AND thab.account_id = p_account_id
       AND h.posted_at >= p_from_posted_at;

    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after, net_amount)
    SELECT w.header_id, p_account_id, v_ledger_id, w.balance_after, w.net_amount
      FROM balance_walk(NULL, p_account_id, p_from_posted_at, v_starting) w;
END;
$function$;

COMMENT ON FUNCTION balance_walk(UUID, UUID, TIMESTAMPTZ, NUMERIC) IS
    'The single running-balance implementation (mig 211). Set-based and scope-'
    'parameterized so one account and a whole ledger are the same query: the '
    'per-account and whole-ledger recomputes are both thin persists over this, and the '
    'read-only consistency check reads it directly. Writes nothing. Since mig 277 it '
    'reads its headers from balance_walk_headers, the definition the per-account '
    'recompute''s seed shares.';

COMMENT ON FUNCTION fn_recompute_balances_for_account(UUID, TIMESTAMPTZ) IS
    'ADR-0034 balance writer for one account from an anchor. Seeds from the last stored '
    'balance before the anchor (else opening_balance, else 0), then replaces the window '
    'with what balance_walk derives. Since mig 211 it shares that one walk with the '
    'whole-ledger recompute, so the two cannot disagree. Since mig 275 it takes the '
    'account''s ledger write lock (fn_ledger_write_lock) before it reads the seed: nothing '
    'for a writer that took it first, this statement''s for a statement of its own (the '
    'balances repair), refused in a transaction that wrote without it. Since mig 277 the '
    'seed is read only from a running balance the walk writes (a header in '
    'balance_walk_headers with a line on the account), and the window is cleared of every '
    'stored row, on any header, so what remains there is exactly what the walk writes.';

COMMIT;
