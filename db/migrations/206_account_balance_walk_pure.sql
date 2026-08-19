-- =============================================================================
-- 206 — extract the running-balance calculation into a PURE function
-- =============================================================================
--
-- WHY. `txn_header_account_balances` is a denormalised running total maintained
-- by `LegDerivedRecomputeInterceptor` on every EF save. Any write that bypasses
-- the ChangeTracker — raw SQL, Dapper, ExecuteUpdateAsync, a human at a psql
-- prompt — skips it, and the stored totals silently diverge from what the legs
-- say. That is not hypothetical: a one-off data scrub reshaped in-kind transfers
-- on three accounts, correctly recomputed the FIFO side, and never touched
-- balances. The register showed wrong figures on those accounts for months, and
-- it surfaced only because someone happened to run the balance health check.
--
-- The reason it stayed invisible is that there was no way to ASK. The only
-- implementation of the running-sum rules lived inside
-- `fn_recompute_balances_for_account`, which DELETEs and INSERTs — so the only
-- way to find out whether a stored balance was right was to overwrite it. The
-- health check "worked" by recomputing everything and diffing against a snapshot
-- taken beforehand: a diagnostic that healed 2,741 rows as a side effect of
-- being asked a question.
--
-- WHAT. Split calculation from persistence, the way migration 202 did for the
-- FIFO walk:
--
--   * `account_balance_walk(account, from, starting)` — STABLE, writes nothing,
--     returns the header-by-header running balance.
--   * `fn_recompute_balances_for_account` becomes a thin DELETE + INSERT over
--     it, so there is exactly ONE implementation of the rules and a read-only
--     check cannot drift from the writer.
--
-- The seed is a PARAMETER rather than something the walk looks up, and that is
-- the crux. The writer wants an incremental window, so it seeds from the last
-- stored row before the anchor. A consistency check cannot do that — seeding
-- from stored state is exactly what it is trying to verify — so it walks the
-- whole account from `accounts.opening_balance`. Same rules, different seed.
--
-- The rules themselves are unchanged and must stay in step with the resolved
-- view layer (mig 124): merged headers excluded, effective is_hidden honoured,
-- leg and header overrides applied, ordering by effective posted_at then seq.
-- =============================================================================

BEGIN;

CREATE FUNCTION account_balance_walk(
    p_account_id       UUID,
    p_from_posted_at   TIMESTAMPTZ,
    p_starting_balance NUMERIC
) RETURNS TABLE (
    header_id     UUID,
    posted_at     TIMESTAMPTZ,
    seq           BIGINT,
    net_amount    NUMERIC,
    balance_after NUMERIC
)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    WITH header_net AS (
        SELECT h.id                              AS header_id,
               COALESCE(o.posted_at, h.posted_at) AS posted_at,
               h.seq                             AS seq,
               SUM(COALESCE(lo.amount, l.amount)) AS net_amount
          FROM live_txn_headers h
          JOIN txn_legs l               ON l.header_id = h.id
          LEFT JOIN txn_leg_overrides lo    ON lo.leg_id  = l.id
          LEFT JOIN txn_header_overrides o  ON o.header_id = h.id
         WHERE l.account_id = p_account_id
           AND h.is_merged_into IS NULL
           AND COALESCE(o.is_hidden, h.is_hidden, FALSE) = FALSE
           AND COALESCE(o.posted_at, h.posted_at) >= p_from_posted_at
         GROUP BY h.id, COALESCE(o.posted_at, h.posted_at), h.seq
    )
    SELECT header_net.header_id,
           header_net.posted_at,
           header_net.seq,
           header_net.net_amount,
           p_starting_balance + SUM(header_net.net_amount) OVER (
               ORDER BY header_net.posted_at, header_net.seq
               ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
           ) AS balance_after
      FROM header_net;
$$;

COMMENT ON FUNCTION account_balance_walk(UUID, TIMESTAMPTZ, NUMERIC) IS
    'Pure running-balance walk (mig 206). Writes nothing. The seed is a '
    'parameter so the incremental writer and a whole-account consistency check '
    'share one implementation of the rules.';

-- The writer, now a thin persist over the walk. Behaviour is unchanged: same
-- seed lookup, same delete window, same rows inserted.
CREATE OR REPLACE FUNCTION fn_recompute_balances_for_account(
    p_account_id     UUID,
    p_from_posted_at TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
    v_starting  NUMERIC(19, 4);
    v_ledger_id UUID;
BEGIN
    SELECT a.ledger_id INTO v_ledger_id FROM accounts a WHERE a.id = p_account_id;
    IF v_ledger_id IS NULL THEN
        RETURN;
    END IF;

    -- Seed: the last stored balance strictly before the anchor, else the
    -- account's opening balance.
    SELECT thab.balance_after
      INTO v_starting
      FROM txn_header_account_balances thab
      JOIN live_txn_headers h ON h.id = thab.header_id
      LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     WHERE thab.account_id = p_account_id
       AND COALESCE(o.posted_at, h.posted_at) < p_from_posted_at
     ORDER BY COALESCE(o.posted_at, h.posted_at) DESC, h.seq DESC
     LIMIT 1;

    IF v_starting IS NULL THEN
        SELECT a.opening_balance INTO v_starting FROM accounts a WHERE a.id = p_account_id;
    END IF;
    v_starting := COALESCE(v_starting, 0);

    DELETE FROM txn_header_account_balances thab
     USING live_txn_headers h
      LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     WHERE thab.header_id = h.id
       AND thab.account_id = p_account_id
       AND COALESCE(o.posted_at, h.posted_at) >= p_from_posted_at;

    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after, net_amount)
    SELECT w.header_id, p_account_id, v_ledger_id, w.balance_after, w.net_amount
      FROM account_balance_walk(p_account_id, p_from_posted_at, v_starting) w;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------
-- Realized gains, as a TABLE rather than the composite array holdings_fifo_walk
-- returns. Same source, unnested: the consistency check needs one ROW per
-- disposal because that is the grain `realized_gains` stores, and mig 205 rounds
-- each row as it is written. Comparing a rounded SUM against a SUM of rounded
-- rows differs by a cent per disposal and would make the check cry wolf -- an
-- ad-hoc query made exactly that mistake while diagnosing this.
-- ---------------------------------------------------------------------------
CREATE FUNCTION realized_gains_walk(
    p_account_id  UUID,
    p_security_id UUID
) RETURNS TABLE (
    sell_leg_id     UUID,
    sold_at         TIMESTAMPTZ,
    quantity        NUMERIC,
    proceeds        NUMERIC,
    cost_basis_sold NUMERIC,
    realized_gain   NUMERIC
)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    SELECT (g).sell_leg_id,
           (g).sold_at,
           ROUND((g).quantity, 12),
           ROUND((g).proceeds, 2),
           ROUND((g).cost_basis_sold, 2),
           ROUND((g).realized_gain, 2)
      FROM holdings_fifo_walk(p_account_id, p_security_id, NULL) w,
           unnest(w.o_gains) AS g;
$$;

COMMENT ON FUNCTION realized_gains_walk(UUID, UUID) IS
    'Pure per-disposal realized gains (mig 206), rounded exactly as mig 205 '
    'rounds them on write, so a consistency check compares like for like.';

COMMIT;
