-- 203 — one implementation per rule: drop the superseded as-of feeders
--
-- Migrations 200 and 201 added BATCHED as-of feeders and left the per-instant forms
-- in place, described as "references" for the equivalence tests. That is duplication
-- with a flattering name: two implementations of the same rule, held in step by a
-- test rather than by construction. The batched forms subsume the others exactly —
-- a single instant is an array of one element — so the others are dropped.
--
-- DROPPED, with every caller moved to the batched form first:
--   holdings_market_value_as_of(uuid, timestamptz, uuid, uuid)   -- mig 172
--   account_balance_as_of(uuid, timestamptz, uuid)               -- mig 173/198
--   account_balance_as_of_set(uuid, timestamptz, uuid[])         -- mig 199
--
-- No SQL callers existed; the C# ones are net_worth_history, holdings_snapshot,
-- allocation and the returns valuations.
--
-- WHY IT MATTERS BEYOND TIDINESS. net_worth_history called BOTH per-instant feeders
-- inside a loop over its period ends — the identical shape that made a whole-ledger
-- TWR cost ~60 s and that migrations 200/201 exist to remove. Its MaxHistoryPoints
-- cap is the same species as the MaxReturnsBoundaries cap deleted in 0.62.0: a limit
-- that exists because the call underneath is O(instants) round trips. Moving it onto
-- the batched feeders makes it one query per feeder for the whole series.
--
-- The equivalence suites lose their reference implementation by design. Their
-- SCENARIOS are the valuable part — splits, fractional and reverse ratios, a split
-- sharing an instant with a trade, positions closed mid-window, a price observed
-- across a split, trade-only pricing, posted-at overrides, seq ties, opening-balance
-- fallback — so they become value-asserting tests over the same data instead.

-- p_account_ids NULL now means EVERY account, which net_worth_history needs (it
-- values the whole ledger). An empty array still means no accounts. Without this the
-- only way to ask for "all" would be to enumerate them, which is what the dropped
-- 3-argument account_balance_as_of existed to avoid.
CREATE OR REPLACE FUNCTION account_balance_as_of_instants(
    p_ledger_id   UUID,
    p_as_ofs      TIMESTAMPTZ[],
    p_account_ids UUID[] DEFAULT NULL
)
RETURNS TABLE(as_of TIMESTAMPTZ, account_id UUID, balance NUMERIC)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
WITH asks AS (
    SELECT DISTINCT u AS at FROM unnest(p_as_ofs) AS u WHERE u IS NOT NULL
),
horizon AS (
    SELECT MAX(at) AS t_max FROM asks
),
scoped AS (
    SELECT a.id, a.opening_balance
    FROM accounts a
    WHERE a.ledger_id = p_ledger_id
      AND (p_account_ids IS NULL OR a.id = ANY (p_account_ids))
),
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
stream AS (
    SELECT account_id, effective_at, 0 AS is_ask, seq, balance_after,
           NULL::TIMESTAMPTZ AS ask_at
    FROM balance_rows
    UNION ALL
    SELECT s.id, a.at, 1, NULL::BIGINT, NULL::NUMERIC, a.at
    FROM scoped s CROSS JOIN asks a
),
islands AS (
    SELECT account_id, ask_at, balance_after,
           COUNT(balance_after) OVER (
               PARTITION BY account_id ORDER BY effective_at, is_ask, seq NULLS LAST
           ) AS island
    FROM stream
),
filled AS (
    SELECT account_id, ask_at, ff_balance
    FROM (
        SELECT account_id, ask_at,
               MAX(balance_after) OVER (PARTITION BY account_id, island) AS ff_balance
        FROM islands
    ) x
    WHERE x.ask_at IS NOT NULL
)
SELECT f.ask_at AS as_of,
       f.account_id,
       COALESCE(f.ff_balance, s.opening_balance) AS balance
FROM filled f
JOIN scoped s ON s.id = f.account_id;
$$;

COMMENT ON FUNCTION account_balance_as_of_instants(UUID, TIMESTAMPTZ[], UUID[]) IS
    'Cash balances for MANY instants in one pass (mig 201; NULL account set = every '
    'account since mig 203). The sole as-of balance feeder — the per-instant '
    'account_balance_as_of / _set forms it replaced were dropped in mig 203.';

DROP FUNCTION IF EXISTS holdings_market_value_as_of(UUID, TIMESTAMPTZ, UUID, UUID);
DROP FUNCTION IF EXISTS account_balance_as_of_set(UUID, TIMESTAMPTZ, UUID[]);
DROP FUNCTION IF EXISTS account_balance_as_of(UUID, TIMESTAMPTZ, UUID);
