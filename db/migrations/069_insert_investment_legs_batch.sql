-- =============================================================================
-- 069 — insert_investment_legs(jsonb): single-statement batch leg insert
-- =============================================================================
--
-- THE PERF PROBLEM (continued from 067/068)
--
-- After 067/068 narrowed the recompute scope, an investment Buy save
-- still took ~3.5 s. Diagnosis: EF Core + Npgsql batches per-row
-- INSERTs into one network round trip BUT emits N sequential
-- `INSERT INTO txn_legs ... VALUES (...);` statements separated by
-- `;`. Postgres treats each as a separate statement, so per-statement
-- AFTER triggers fire N times. A 4-leg Buy fires:
--   * `trg_legs_balance_after_insert` 4 times — each walks balance
--     forward from the account's earliest affected posted_at
--   * `trg_txn_legs_recompute_insert` 4 times — each calls
--     `recompute_holdings_cost_basis(...)`
-- Trigger fan-out dominates the save latency.
--
-- THE FIX
--
-- This migration adds `insert_investment_legs(p_legs JSONB)` — a
-- PL/pgSQL function that does ONE `INSERT INTO txn_legs ... SELECT
-- FROM jsonb_to_recordset(...)` for the whole batch. Postgres sees
-- one statement → AFTER STATEMENT triggers fire ONCE → balance and
-- recompute walks happen once instead of N times.
--
-- The function returns the inserted row count so EF Core can bind it
-- via `HasDbFunction` and invoke through LINQ (no FromSqlRaw /
-- ExecuteSqlInterpolated needed in the repository — the SQL body
-- lives here in the migration). See AppDbContext for the binding.
--
-- WHY THIS FUNCTION + NOT JUST EF'S BATCH BEHAVIOR
--
-- EF Core's default Npgsql batch mode is to send sequential per-row
-- INSERTs in one round trip — fine for latency on simple writes, but
-- per-statement triggers see them as separate. There is no native
-- EF/Npgsql knob today for emitting multi-row `VALUES (a),(b),(c)`
-- syntax. Custom SQL is the only path, and per the no-raw-sql-in-API
-- policy that SQL lives here.
--
-- BALANCE_AFTER + CREATED_AT
--
-- * `balance_after` is left NULL on insert; `trg_legs_balance_after_insert`
--   fills it post-statement (existing behavior — the trigger has been
--   the authoritative writer for this column since before 068).
-- * `created_at` is set to `clock_timestamp()` per row (matches
--   `DateTime.UtcNow` semantics the API previously used per-row).
-- =============================================================================

CREATE OR REPLACE FUNCTION insert_investment_legs(p_legs JSONB)
RETURNS TABLE(inserted_count INTEGER)
LANGUAGE plpgsql
AS $$
DECLARE
    v_count INTEGER;
BEGIN
    -- Single multi-row INSERT. jsonb_to_recordset projects the JSON
    -- array into a typed result set so PL/pgSQL doesn't need an
    -- explicit cast per column.
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
    FROM jsonb_to_recordset(p_legs) AS r(
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

COMMENT ON FUNCTION insert_investment_legs(JSONB) IS
    'Single-statement batched insert into txn_legs (069). Replaces the '
    'API repository''s per-row Add() loop so per-statement AFTER triggers '
    '(balance-after recompute, holdings recompute) fire once per save '
    'instead of once per leg. Returns inserted row count; bound via '
    'AppDbContext.HasDbFunction for LINQ invocation.';
