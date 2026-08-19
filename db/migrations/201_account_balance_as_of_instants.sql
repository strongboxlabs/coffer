-- 201 — batched cash balances: every requested instant in ONE pass
--
-- The last piece of the TWR boundary cost, and the reason MaxReturnsBoundaries can
-- now be deleted rather than retuned a fourth time.
--
-- Migration 200 batched the holdings half of a boundary valuation: 420 instants
-- went from 7,398 ms to 698 ms, ~1.7 ms each. That relocated the cost rather than
-- removing it — the stress lane's end-to-end figure was still 27.9 ms per
-- boundary, because account_balance_as_of_set was called ONCE PER BOUNDARY, each
-- call re-running a LATERAL per account in scope. 400 boundaries meant 400 round
-- trips over the same balance rows.
--
-- Same treatment as mig 200: merge the requested instants into the balance stream
-- as pseudo-rows, sort once, and forward-fill. Each account's balance rows and the
-- instants share one ordering; COUNT() over it numbers the islands between real
-- balance rows, and FIRST_VALUE() inside an island hands each instant the
-- balance_after that opened it. Accounts with no row before an instant fall back to
-- opening_balance, exactly as the per-instant form does.
--
-- ORDERING IS THE WHOLE CORRECTNESS ARGUMENT, and it must reproduce mig 199's
-- `ORDER BY COALESCE(o.posted_at, h.posted_at) DESC, h.seq DESC` — including the
-- posted-at OVERRIDE (mig 173). The override matters more than it looks: it can
-- move a header's effective date without changing its relative order, which is
-- what disproved an earlier attempt to denormalise this ordering onto
-- txn_header_account_balances. So the effective date is computed here, in the
-- stream, and never assumed to agree with h.posted_at.
--
-- An instant sharing its effective date with balance rows must see them, so the
-- pseudo-rows sort AFTER real rows at the same date (is_ask = 1) and after every
-- seq. Ties among balance rows at one date are broken by seq, so the island an
-- instant falls into is opened by the highest-seq row at or before it — the row
-- mig 199's DESC/DESC/LIMIT 1 picks.
--
-- account_balance_as_of (mig 198) and account_balance_as_of_set (mig 199) are both
-- untouched; this is purely additive, and mig 199 remains the reference an
-- equivalence test compares against.

CREATE FUNCTION account_balance_as_of_instants(
    p_ledger_id   UUID,
    p_as_ofs      TIMESTAMPTZ[],
    p_account_ids UUID[]
)
RETURNS TABLE(as_of TIMESTAMPTZ, account_id UUID, balance NUMERIC)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
WITH asks AS (
    SELECT DISTINCT u AS at
    FROM unnest(p_as_ofs) AS u
    WHERE u IS NOT NULL
),
horizon AS (
    SELECT MAX(at) AS t_max FROM asks
),
scoped AS (
    SELECT a.id, a.opening_balance
    FROM accounts a
    WHERE a.ledger_id = p_ledger_id
      AND a.id = ANY (p_account_ids)
),
-- Balance rows up to the last requested instant, on their EFFECTIVE date.
balance_rows AS (
    SELECT thab.account_id,
           COALESCE(o.posted_at, h.posted_at) AS effective_at,
           h.seq,
           thab.balance_after
    FROM txn_header_account_balances thab
    JOIN txn_headers h ON h.id = thab.header_id
    LEFT JOIN txn_header_overrides o ON o.header_id = h.id
    CROSS JOIN horizon
    WHERE thab.account_id IN (SELECT id FROM scoped)
      AND COALESCE(o.posted_at, h.posted_at) <= horizon.t_max
),
-- Balance rows and instants in one stream. is_ask = 1 sorts an instant after every
-- balance row sharing its effective date, which is what `<= p_as_of` means.
stream AS (
    SELECT account_id, effective_at, 0 AS is_ask, seq, balance_after,
           NULL::TIMESTAMPTZ AS ask_at
    FROM balance_rows
    UNION ALL
    SELECT s.id, a.at, 1, NULL::BIGINT, NULL::NUMERIC, a.at
    FROM scoped s
    CROSS JOIN asks a
),
islands AS (
    SELECT account_id, ask_at, balance_after,
           COUNT(balance_after) OVER (
               PARTITION BY account_id
               ORDER BY effective_at, is_ask, seq NULLS LAST
           ) AS island
    FROM stream
),
filled AS (
    SELECT account_id, ask_at, ff_balance
    FROM (
        -- COUNT() increments on each non-null balance, so every island is opened by
        -- exactly ONE balance row and followed by the instants that fall after it.
        -- MAX over the island picks that single value — island 0 has no balance row
        -- at all and yields NULL, which the outer COALESCE turns into the opening
        -- balance. (Ordering by the balance VALUE here, as a first draft did, would
        -- pick the largest balance in the island rather than the applicable one.)
        SELECT account_id, ask_at,
               MAX(balance_after) OVER (PARTITION BY account_id, island) AS ff_balance
        FROM islands
    ) x
    WHERE x.ask_at IS NOT NULL
)
-- No balance row before the instant → the account's opening balance, matching the
-- COALESCE(latest.balance_after, a.opening_balance) of the per-instant form.
SELECT f.ask_at AS as_of,
       f.account_id,
       COALESCE(f.ff_balance, s.opening_balance) AS balance
FROM filled f
JOIN scoped s ON s.id = f.account_id;
$$;

COMMENT ON FUNCTION account_balance_as_of_instants(UUID, TIMESTAMPTZ[], UUID[]) IS
    'Cash balances for a set of accounts at MANY instants in one pass (mig 201). '
    'Row-for-row equivalent to calling account_balance_as_of_set once per instant, '
    'but merges the instants into the balance stream and forward-fills, so 400 TWR '
    'boundaries cost one query instead of 400. Honours the posted-at override '
    '(mig 173) by computing the effective date in the stream.';

GRANT EXECUTE ON FUNCTION account_balance_as_of_instants(UUID, TIMESTAMPTZ[], UUID[]) TO coffer_app;
