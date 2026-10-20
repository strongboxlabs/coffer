-- =============================================================================
-- 099 — Recompute honors txn_header_overrides.posted_at (ADR-0034 known caveat)
-- =============================================================================
--
-- Closes the loose end called out in ADR-0034's Consequences: the
-- balance trigger reads h.posted_at directly, but resolved_transactions
-- exposes COALESCE(o.posted_at, h.posted_at). When a user overrides a
-- header's date via txn_header_overrides, the running balance silently
-- stays in the old order — balance values appear next to one date
-- visually while the trigger computed them assuming a different date.
--
-- Two changes:
--   1. Update fn_recompute_balances_for_account to use
--      COALESCE(o.posted_at, h.posted_at) in every place h.posted_at
--      participated in anchor / window / sort.
--   2. New statement-level triggers on txn_header_overrides
--      (INSERT/UPDATE-of-posted_at/DELETE) that re-anchor at the
--      EARLIEST of (old override posted_at, new override posted_at,
--      h.posted_at) so the recompute window catches both the OLD
--      effective position and the NEW.
--
-- Posted_at overrides are still rare in practice; this trigger fires
-- only when the user explicitly edits a header's date through the
-- override surface.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1) Recompute function honours override.posted_at via LEFT JOIN +
--    COALESCE. Anchor / wipe / aggregation / sort all share the same
--    effective-posted_at expression.
-- -----------------------------------------------------------------------------

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

    -- Anchor: balance after the last header strictly before the
    -- recompute window. The "effective" posted_at uses the override
    -- when present (mig 099).
    SELECT thab.balance_after
      INTO v_starting
      FROM txn_header_account_balances thab
      JOIN txn_headers h ON h.id = thab.header_id
      LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     WHERE thab.account_id = p_account_id
       AND COALESCE(o.posted_at, h.posted_at) < p_from_posted_at
     ORDER BY COALESCE(o.posted_at, h.posted_at) DESC, h.seq DESC
     LIMIT 1;

    IF v_starting IS NULL THEN
        SELECT a.opening_balance INTO v_starting FROM accounts a WHERE a.id = p_account_id;
    END IF;
    v_starting := COALESCE(v_starting, 0);

    -- Wipe the window for this account.
    DELETE FROM txn_header_account_balances thab
     USING txn_headers h
      LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     WHERE thab.header_id = h.id
       AND thab.account_id = p_account_id
       AND COALESCE(o.posted_at, h.posted_at) >= p_from_posted_at;

    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after, net_amount)
    WITH header_net AS (
        SELECT h.id AS header_id,
               COALESCE(o.posted_at, h.posted_at) AS posted_at,
               h.seq,
               SUM(l.amount) AS net_amount
          FROM txn_headers h
          JOIN txn_legs l ON l.header_id = h.id
          LEFT JOIN txn_header_overrides o ON o.header_id = h.id
         WHERE l.account_id = p_account_id
           AND h.is_merged_into IS NULL
           AND COALESCE(o.posted_at, h.posted_at) >= p_from_posted_at
         GROUP BY h.id, COALESCE(o.posted_at, h.posted_at), h.seq
    )
    SELECT
        header_id,
        p_account_id,
        v_ledger_id,
        v_starting + SUM(net_amount) OVER (
            ORDER BY posted_at, seq
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
        ) AS balance_after,
        net_amount
      FROM header_net;
END;
$$ LANGUAGE plpgsql;

-- -----------------------------------------------------------------------------
-- 2) Statement-level triggers on txn_header_overrides. Three flavors
--    (INSERT / UPDATE / DELETE) because Postgres transition tables are
--    op-specific in their REFERENCING clause.
-- -----------------------------------------------------------------------------

-- INSERT: new override row introduces an effective posted_at. Recompute
-- anchored at MIN(new.posted_at, h.posted_at) — both old and new
-- positions need to be re-evaluated.
CREATE OR REPLACE FUNCTION fn_trg_header_overrides_insert_recompute()
RETURNS TRIGGER AS $$
DECLARE
    rec RECORD;
BEGIN
    IF pg_trigger_depth() > 1 THEN
        RETURN NULL;
    END IF;

    FOR rec IN
        SELECT l.account_id, MIN(LEAST(n.posted_at, h.posted_at)) AS dt
          FROM new_rows n
          JOIN txn_headers h ON h.id = n.header_id
          JOIN txn_legs l ON l.header_id = n.header_id
         WHERE n.posted_at IS NOT NULL
         GROUP BY l.account_id
    LOOP
        PERFORM fn_recompute_balances_for_account(rec.account_id, rec.dt);
    END LOOP;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- UPDATE: only fires when posted_at changed (column-level trigger
-- clause). Anchor at MIN(old.posted_at, new.posted_at, h.posted_at) —
-- old position must be re-evaluated alongside the new.
CREATE OR REPLACE FUNCTION fn_trg_header_overrides_update_recompute()
RETURNS TRIGGER AS $$
DECLARE
    rec RECORD;
BEGIN
    IF pg_trigger_depth() > 1 THEN
        RETURN NULL;
    END IF;

    IF NOT EXISTS (
        SELECT 1
          FROM new_rows n
          JOIN old_rows o ON o.header_id = n.header_id
         WHERE n.posted_at IS DISTINCT FROM o.posted_at
    ) THEN
        RETURN NULL;
    END IF;

    FOR rec IN
        SELECT l.account_id,
               MIN(LEAST(
                   COALESCE(n.posted_at, h.posted_at),
                   COALESCE(o.posted_at, h.posted_at)
               )) AS dt
          FROM new_rows n
          JOIN old_rows o ON o.header_id = n.header_id
          JOIN txn_headers h ON h.id = n.header_id
          JOIN txn_legs l ON l.header_id = n.header_id
         GROUP BY l.account_id
    LOOP
        PERFORM fn_recompute_balances_for_account(rec.account_id, rec.dt);
    END LOOP;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

-- DELETE: override row removed; effective posted_at reverts to
-- h.posted_at. Anchor at MIN(old.posted_at, h.posted_at).
CREATE OR REPLACE FUNCTION fn_trg_header_overrides_delete_recompute()
RETURNS TRIGGER AS $$
DECLARE
    rec RECORD;
BEGIN
    IF pg_trigger_depth() > 1 THEN
        RETURN NULL;
    END IF;

    FOR rec IN
        SELECT l.account_id, MIN(LEAST(o.posted_at, h.posted_at)) AS dt
          FROM old_rows o
          JOIN txn_headers h ON h.id = o.header_id
          JOIN txn_legs l ON l.header_id = o.header_id
         WHERE o.posted_at IS NOT NULL
         GROUP BY l.account_id
    LOOP
        PERFORM fn_recompute_balances_for_account(rec.account_id, rec.dt);
    END LOOP;
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_header_overrides_recompute_insert
AFTER INSERT ON txn_header_overrides
REFERENCING NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_header_overrides_insert_recompute();

CREATE TRIGGER trg_header_overrides_recompute_update
AFTER UPDATE ON txn_header_overrides
REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_header_overrides_update_recompute();

CREATE TRIGGER trg_header_overrides_recompute_delete
AFTER DELETE ON txn_header_overrides
REFERENCING OLD TABLE AS old_rows
FOR EACH STATEMENT
EXECUTE FUNCTION fn_trg_header_overrides_delete_recompute();

-- -----------------------------------------------------------------------------
-- 3) One-shot recompute. The function body changed (anchor / window /
--    sort now use COALESCE), so any header with an existing override
--    whose posted_at differs from h.posted_at has a stale balance.
--    Walk every account and re-derive.
-- -----------------------------------------------------------------------------
DO $$
DECLARE
    v_account_id UUID;
BEGIN
    FOR v_account_id IN
        SELECT DISTINCT account_id FROM txn_legs
    LOOP
        PERFORM fn_recompute_balances_for_account(
            v_account_id,
            '0001-01-01'::TIMESTAMPTZ
        );
    END LOOP;
END $$;
