-- 211 — ONE running-balance implementation, not two
--
-- The arithmetic that turns legs into running balances existed twice:
--
--   * account_balance_walk (mig 206)          — per account, seed passed in
--   * fn_recompute_balances_for_ledger (188)  — whole ledger, set-based, seeds each
--                                               account from its own opening_balance
--
-- Both walk live_txn_headers, both exclude merged and hidden headers, both use the
-- override-aware posted_at, both accumulate SUM(...) OVER (ORDER BY posted_at, seq).
-- Two independent statements of one rule.
--
-- WHY THAT IS THE DEFECT AND NOT JUST UNTIDY. Migration 206 rewrote the per-account
-- side and did not touch the ledger-wide twin. They still agreed — verified at the
-- time on 102,249 real rows — but nothing made them agree, and the guard was a test
-- comparing one against the other. That test is the wrong shape: it makes the
-- duplication permanent and merely alarms when the copies drift, and it names the
-- per-account function "the reference implementation" while 206 was busy changing it.
-- The failure it was watching for is nasty precisely because both answers look right:
-- a restored ledger would disagree with the same ledger after one edit, and the
-- consistency checker would report drift that is really a function mismatch.
--
-- WHICH ONE SURVIVES, and this is measured rather than chosen. Delegating the
-- ledger-wide path to the per-account walk (CROSS JOIN LATERAL over accounts) puts
-- back exactly the per-account overhead mig 188 was written to remove: ~12ms per
-- account, so ~7.6s for a real 633-account ledger against ~325ms for the set-based
-- pass. Only the set-based form can serve both shapes, so it is the survivor, and the
-- per-account case becomes that same query restricted to one account.
--
-- WHAT EACH CALLER NOW IS:
--
--   balance_walk(ledger, account, from, starting)   the single implementation
--     * ledger NULL + account set  -> one account
--     * ledger set + account NULL  -> every account in the ledger
--     * starting NULL              -> each account seeds from its own opening_balance
--     * starting given             -> that scalar seeds the (single) account
--
--   account_balance_walk(account, from, starting)   thin wrapper, unchanged signature
--     and unchanged 5-column shape, so the EF binding (AppDbContext.AccountBalanceWalk)
--     and the consistency checker need no C# change at all.
--
--   fn_recompute_balances_for_account(account, from)  same seed logic as before —
--     last stored balance strictly before the anchor, else opening_balance, else 0 —
--     then DELETE the window and INSERT from balance_walk.
--
--   fn_recompute_balances_for_ledger(ledger)  INSERT from balance_walk over the whole
--     ledger. Its contract is unchanged, INCLUDING that the caller has already cleared
--     txn_header_account_balances for the ledger (the snapshot restore in migs 188/193
--     does exactly that before calling), because changing that would change behaviour
--     for a path this migration has no business touching.
--
-- Equivalence is proven against real data before this ships, not asserted: the new
-- functions must reproduce, row for row, what the old ones stored for a 102,249-row
-- ledger — in both directions.
-- ---------------------------------------------------------------------------

BEGIN;

-- ---------------------------------------------------------------------------
-- The one implementation.
-- ---------------------------------------------------------------------------
CREATE FUNCTION balance_walk(
    p_ledger_id        UUID,
    p_account_id       UUID,
    p_from_posted_at   TIMESTAMPTZ,
    p_starting_balance NUMERIC
) RETURNS TABLE (
    account_id    UUID,
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
        SELECT l.account_id,
               -- Carried through the GROUP BY rather than joined again in the outer
               -- query: it is functionally dependent on account_id, and one join to
               -- accounts serves both the ledger filter and the per-account seed.
               COALESCE(a.opening_balance, 0)      AS opening_balance,
               h.id                               AS header_id,
               COALESCE(o.posted_at, h.posted_at) AS posted_at,
               h.seq                              AS seq,
               SUM(COALESCE(lo.amount, l.amount)) AS net_amount
          FROM live_txn_headers h
          JOIN txn_legs l                   ON l.header_id = h.id
          JOIN accounts a                   ON a.id = l.account_id
          LEFT JOIN txn_leg_overrides lo     ON lo.leg_id  = l.id
          LEFT JOIN txn_header_overrides o   ON o.header_id = h.id
         WHERE (p_account_id IS NULL OR l.account_id = p_account_id)
           -- Scoped through accounts, the way mig 188 scoped it, rather than through
           -- txn_legs.ledger_id. The two agree on well-formed data; the account's own
           -- ledger is the authority on which ledger a balance belongs to.
           AND (p_ledger_id IS NULL OR a.ledger_id = p_ledger_id)
           AND h.is_merged_into IS NULL
           AND COALESCE(o.is_hidden, h.is_hidden, FALSE) = FALSE
           AND COALESCE(o.posted_at, h.posted_at) >= p_from_posted_at
         GROUP BY l.account_id, COALESCE(a.opening_balance, 0), h.id,
                  COALESCE(o.posted_at, h.posted_at), h.seq
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
$$;

COMMENT ON FUNCTION balance_walk(UUID, UUID, TIMESTAMPTZ, NUMERIC) IS
    'The single running-balance implementation (mig 211). Set-based and scope-'
    'parameterized so one account and a whole ledger are the same query: the '
    'per-account and whole-ledger recomputes are both thin persists over this, and the '
    'read-only consistency check reads it directly. Writes nothing.';

-- ---------------------------------------------------------------------------
-- Compatibility wrapper: same name, same arguments, same 5 columns as mig 206, so
-- nothing in C# changes. Kept rather than removed because the EF binding names it and
-- the consistency check reads it; it is now one line over balance_walk.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION account_balance_walk(
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
    SELECT w.header_id, w.posted_at, w.seq, w.net_amount, w.balance_after
      FROM balance_walk(NULL, p_account_id, p_from_posted_at, p_starting_balance) w;
$$;

COMMENT ON FUNCTION account_balance_walk(UUID, TIMESTAMPTZ, NUMERIC) IS
    'One account''s running balance. Since mig 211 a thin wrapper over balance_walk, '
    'kept for its stable signature: the EF binding and the consistency check name it.';

-- ---------------------------------------------------------------------------
-- Per-account recompute: seed logic unchanged from mig 206, rows now from balance_walk.
-- ---------------------------------------------------------------------------
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
      FROM balance_walk(NULL, p_account_id, p_from_posted_at, v_starting) w;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION fn_recompute_balances_for_account(UUID, TIMESTAMPTZ) IS
    'ADR-0034 balance writer for one account from an anchor. Seeds from the last stored '
    'balance before the anchor (else opening_balance, else 0), then replaces the window '
    'with what balance_walk derives. Since mig 211 it shares that one walk with the '
    'whole-ledger recompute, so the two cannot disagree.';

-- ---------------------------------------------------------------------------
-- Whole-ledger recompute: same contract (caller has cleared the ledger's rows), rows
-- now from the same walk.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_recompute_balances_for_ledger(p_ledger_id uuid)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    -- No seed passed: the 0001-01-01 floor means no account has a prior balance to
    -- carry, so each starts from its own opening_balance.
    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after, net_amount)
    SELECT w.header_id, w.account_id, p_ledger_id, w.balance_after, w.net_amount
      FROM balance_walk(p_ledger_id, NULL, '0001-01-01'::timestamptz, NULL) w;
END;
$$;

COMMENT ON FUNCTION fn_recompute_balances_for_ledger(uuid) IS
    'Whole-ledger balance rebuild for snapshot restore. Assumes the caller has already '
    'cleared txn_header_account_balances for the ledger. Since mig 211 a thin persist '
    'over balance_walk — the same implementation the per-account recompute uses, so a '
    'restored ledger and an edited one cannot disagree about the arithmetic.';

COMMIT;
