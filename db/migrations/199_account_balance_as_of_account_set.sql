-- =============================================================================
-- 199 — account_balance_as_of: take a SET of accounts, not one or all
-- =============================================================================
--
-- The function has always offered "one account" or "every account in the ledger",
-- and the returns valuation needs neither. It wants the brokerages — 7 of them,
-- or 50 on a real ledger — so it asked for ALL and threw the rest away:
--
--     await _db.AccountBalanceAsOf(ledgerId, asOfUtc, null)
--         .Where(b => brokerageIds.Contains(b.AccountId))
--
-- On a production ledger that is a balance computed for ~663 accounts — 413 of
-- them categories — to use 50. Once per TWR boundary, hundreds of times per
-- report.
--
-- Measured on a 41-account ledger with 100,000 balance rows, summing the 7
-- brokerages:
--
--     compute all, filter after     62.6 ms
--     compute only the 7             0.8 ms      74x
--
-- 41 accounts is the SMALL case. The waste grows with everything the caller does
-- not want.
--
-- This is what actually made whole-ledger returns slow. Migration 198 removed a
-- per-account rescan of txn_headers and was a real fix, but it optimised the
-- shape of a question that should never have been asked at this size. Scoped to
-- the accounts the caller wants, even the pre-198 LATERAL runs in under a
-- millisecond.
--
-- A DISTINCT NAME rather than an overload of account_balance_as_of. Two Postgres
-- functions sharing a name and differing only in arity are fine for Postgres but
-- not for EF: mapping both to "account_balance_as_of" makes the model ambiguous
-- and every query through EITHER of them fails to translate. Existing callers
-- keep the 3-argument function unchanged.
-- =============================================================================

CREATE OR REPLACE FUNCTION account_balance_as_of_set(
    p_ledger_id   UUID,
    p_as_of       TIMESTAMPTZ,
    p_account_ids UUID[]
)
RETURNS TABLE(account_id UUID, balance NUMERIC)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    -- A LATERAL per account, deliberately, NOT the DISTINCT ON that migration 198
    -- gave the all-accounts function. The two shapes win in opposite regimes:
    -- DISTINCT ON sorts every balance row once and is right when the caller wants
    -- most of the ledger; a top-N LATERAL per account is right when it wants a
    -- handful. Measured on the same data for 7 accounts out of 41: DISTINCT ON
    -- 8.5 ms, LATERAL 0.8 ms. This function exists precisely because the caller
    -- knows it wants few, so it takes the shape that suits few.
    SELECT a.id,
           COALESCE(latest.balance_after, a.opening_balance)
      FROM accounts a
      LEFT JOIN LATERAL (
          SELECT thab.balance_after
            FROM txn_header_account_balances thab
            JOIN txn_headers h ON h.id = thab.header_id
            LEFT JOIN txn_header_overrides o ON o.header_id = h.id
           WHERE thab.account_id = a.id
             AND COALESCE(o.posted_at, h.posted_at) <= p_as_of
           ORDER BY COALESCE(o.posted_at, h.posted_at) DESC, h.seq DESC
           LIMIT 1
      ) latest ON TRUE
     WHERE a.ledger_id = p_ledger_id
       AND a.id = ANY(p_account_ids);
$$;

GRANT EXECUTE ON FUNCTION account_balance_as_of_set(UUID, TIMESTAMPTZ, UUID[]) TO coffer_app;

-- account_balance_as_of (mig 198) is untouched and remains what every other
-- caller uses; this migration is purely additive.
