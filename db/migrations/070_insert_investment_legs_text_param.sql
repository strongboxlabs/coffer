-- =============================================================================
-- 070 — insert_investment_legs: accept TEXT, cast to JSONB inside
-- =============================================================================
--
-- Migration 069 declared `insert_investment_legs(p_legs JSONB)`. The
-- EF Core 10 + Npgsql provider's HasDbFunction binding sends string
-- parameters as TEXT regardless of HasParameter().HasStoreType("jsonb")
-- — so the resolved call shape is `insert_investment_legs(text)`,
-- which doesn't match the JSONB signature → `42883: function
-- insert_investment_legs(text) does not exist`.
--
-- This migration replaces the function with a TEXT parameter and
-- casts to JSONB inside the body. Behavior is identical; only the
-- parameter type changes. EF's call now resolves.
-- =============================================================================

DROP FUNCTION IF EXISTS insert_investment_legs(JSONB);

CREATE OR REPLACE FUNCTION insert_investment_legs(p_legs TEXT)
RETURNS TABLE(inserted_count INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
    v_count INTEGER;
BEGIN
    INSERT INTO txn_legs (
        id, header_id, ledger_id, account_id, posting_index,
        amount, security_id, quantity, unit_price,
        leg_memo, posting_role, created_at
    )
    SELECT
        r.id,
        r.header_id,
        r.ledger_id,
        r.account_id,
        r.posting_index,
        r.amount,
        r.security_id,
        r.quantity,
        r.unit_price,
        r.leg_memo,
        r.posting_role,
        clock_timestamp()
    FROM jsonb_to_recordset(p_legs::jsonb) AS r(
        id            UUID,
        header_id     UUID,
        ledger_id     UUID,
        account_id    UUID,
        posting_index INTEGER,
        amount        NUMERIC,
        security_id   UUID,
        quantity      NUMERIC,
        unit_price    NUMERIC,
        leg_memo      TEXT,
        posting_role  TEXT
    );

    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN QUERY SELECT v_count;
END;
$$;

COMMENT ON FUNCTION insert_investment_legs(TEXT) IS
    'Single-statement batched insert into txn_legs (069/070). EF Core '
    'sends the JSON payload as TEXT; we cast to JSONB inside so '
    'jsonb_to_recordset can consume it. Per-statement AFTER triggers '
    '(balance-after, holdings recompute) fire once per save instead '
    'of once per leg. Returns inserted row count; bound via '
    'AppDbContext.HasDbFunction for LINQ invocation.';
