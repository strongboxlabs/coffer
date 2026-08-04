-- Phase 1: running-balance maintenance for transactions.balance_after.
--
-- Design: a statement-level AFTER trigger on (INSERT, UPDATE, DELETE) computes the
-- earliest-affected (account_id, feed_posted_at) pair from the transition tables and
-- recomputes balance_after for every active row at or after that point.
--
-- Notes:
--  * balance_after sums feed_amount across active txns (is_merged_into IS NULL).
--    Rare amount-overrides (transaction_overrides.amount) are NOT reflected here;
--    that is a known caveat documented in §4 of the architecture doc.
--  * The recompute issues an UPDATE on transactions, which would normally re-fire
--    this trigger. Two safeguards prevent recursion:
--      1) pg_trigger_depth() > 1 short-circuits inside the trigger body, so the
--         recompute UPDATE (which fires at depth 2) returns immediately.
--      2) For UPDATEs that don't touch balance-relevant columns, we early-exit by
--         comparing new_rows vs old_rows in the function body. We can't combine
--         AFTER UPDATE OF (columns) with REFERENCING ... TABLE in PostgreSQL
--         (rejected as "transition tables cannot be specified for triggers with
--         column lists"), so the column-level filter lives in PL/pgSQL instead.

-- ---------------------------------------------------------------------------
-- Helper: recompute balance_after for one account from a given date forward.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_recompute_balance_after(
    p_account_id     UUID,
    p_from_posted_at TIMESTAMPTZ
) RETURNS VOID AS $$
DECLARE
    v_starting NUMERIC(19, 4);
BEGIN
    -- Anchor: balance of the latest active txn strictly before our recompute window,
    -- falling back to the account's opening_balance.
    SELECT t.balance_after
      INTO v_starting
      FROM transactions t
     WHERE t.account_id = p_account_id
       AND t.is_merged_into IS NULL
       AND t.feed_posted_at < p_from_posted_at
     ORDER BY t.feed_posted_at DESC, t.id DESC
     LIMIT 1;

    IF v_starting IS NULL THEN
        SELECT a.opening_balance INTO v_starting FROM accounts a WHERE a.id = p_account_id;
    END IF;
    v_starting := COALESCE(v_starting, 0);

    WITH ordered AS (
        SELECT
            t.id,
            v_starting + SUM(t.feed_amount) OVER (
                ORDER BY t.feed_posted_at, t.id
                ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
            ) AS new_balance
          FROM transactions t
         WHERE t.account_id = p_account_id
           AND t.is_merged_into IS NULL
           AND t.feed_posted_at >= p_from_posted_at
    )
    UPDATE transactions t
       SET balance_after = ordered.new_balance
      FROM ordered
     WHERE t.id = ordered.id
       AND t.balance_after IS DISTINCT FROM ordered.new_balance;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------------
-- Statement-level trigger function (one body, three triggers below).
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_trg_balance_after()
RETURNS TRIGGER AS $$
DECLARE
    rec RECORD;
BEGIN
    IF pg_trigger_depth() > 1 THEN
        RETURN NULL;
    END IF;

    IF TG_OP = 'INSERT' THEN
        FOR rec IN
            SELECT account_id, MIN(feed_posted_at) AS dt
              FROM new_rows
             GROUP BY account_id
        LOOP
            PERFORM fn_recompute_balance_after(rec.account_id, rec.dt);
        END LOOP;
    ELSIF TG_OP = 'DELETE' THEN
        FOR rec IN
            SELECT account_id, MIN(feed_posted_at) AS dt
              FROM old_rows
             GROUP BY account_id
        LOOP
            PERFORM fn_recompute_balance_after(rec.account_id, rec.dt);
        END LOOP;
    ELSE  -- UPDATE
        -- Early exit: if no row's balance-relevant columns actually changed,
        -- there is nothing to recompute. (We can't push this filter into the
        -- trigger declaration because column-list UPDATE triggers can't have
        -- transition tables in PostgreSQL.)
        IF NOT EXISTS (
            SELECT 1
              FROM new_rows n
              JOIN old_rows o ON o.id = n.id
             WHERE n.feed_amount     IS DISTINCT FROM o.feed_amount
                OR n.feed_posted_at  IS DISTINCT FROM o.feed_posted_at
                OR n.account_id      IS DISTINCT FROM o.account_id
                OR n.is_merged_into  IS DISTINCT FROM o.is_merged_into
        ) THEN
            RETURN NULL;
        END IF;

        FOR rec IN
            SELECT account_id, MIN(feed_posted_at) AS dt
              FROM (
                  SELECT account_id, feed_posted_at FROM new_rows
                  UNION ALL
                  SELECT account_id, feed_posted_at FROM old_rows
              ) merged
             GROUP BY account_id
        LOOP
            PERFORM fn_recompute_balance_after(rec.account_id, rec.dt);
        END LOOP;
    END IF;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_txn_balance_after_insert
AFTER INSERT ON transactions
REFERENCING NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_balance_after();

CREATE TRIGGER trg_txn_balance_after_delete
AFTER DELETE ON transactions
REFERENCING OLD TABLE AS old_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_balance_after();

CREATE TRIGGER trg_txn_balance_after_update
AFTER UPDATE ON transactions
REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_balance_after();
