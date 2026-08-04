-- =============================================================================
-- 057 — posting_role invariant trigger + expose role in resolved_transactions
-- =============================================================================
--
-- Migration 056 added `txn_legs.posting_role` and stamped values via the
-- importer + a one-shot heuristic backfill. The column was nullable —
-- correctly for legs of non-investment headers (where role doesn't
-- apply) but accidentally for legs of investment headers too, since
-- nothing in the schema enforced the invariant.
--
-- The intended invariant:
--     posting_role IS NOT NULL  ⇔  txn_headers.action IS NOT NULL
--
-- "Leg of an investment header has a role; leg of a non-investment
-- header doesn't." A CHECK constraint can't express this (CHECK only
-- sees the row's own columns); a trigger does the job.
--
-- This migration:
--   1. Re-applies 056's backfill heuristic to any leg that's still
--      NULL inside an investment header (only the 32 demo legs
--      created by post-056 seed runs that hadn't been updated to
--      stamp the role).
--   2. Verifies zero NULL-investment-leg rows remain — fails loudly
--      via RAISE EXCEPTION if any survived. The trigger can't be armed
--      against dirty data.
--   3. Creates `fn_validate_posting_role()` + the trigger.
--   4. Rebuilds `resolved_transactions` to project `posting_role`
--      through to the register's read surface.
-- =============================================================================


-- -----------------------------------------------------------------------------
-- Part 1: Re-backfill any leg still NULL inside an investment header.
--
-- Same logic as 056, guarded by `posting_role IS NULL` so it's a no-op
-- for the 41K+ legs already stamped correctly. Catches the demo legs
-- inserted by post-056 seed runs before the seeds learned to stamp
-- the role themselves.
-- -----------------------------------------------------------------------------

DO $$
DECLARE
    v_security INTEGER;
    v_fee INTEGER;
    v_income INTEGER;
    v_transfer INTEGER;
BEGIN
    WITH sec_postings AS (
        SELECT DISTINCT l.header_id, l.posting_index
        FROM txn_legs l
        JOIN txn_headers h ON h.id = l.header_id
        WHERE h.action IS NOT NULL
          AND l.quantity IS NOT NULL
    )
    UPDATE txn_legs l
    SET posting_role = 'security'
    FROM sec_postings sp
    WHERE l.header_id     = sp.header_id
      AND l.posting_index = sp.posting_index
      AND l.posting_role IS NULL;
    GET DIAGNOSTICS v_security = ROW_COUNT;

    WITH fee_postings AS (
        SELECT DISTINCT l_cat.header_id, l_cat.posting_index
        FROM txn_legs l_cat
        JOIN accounts a_cat ON a_cat.id = l_cat.account_id
        JOIN txn_legs l_cash ON l_cash.header_id     = l_cat.header_id
                            AND l_cash.posting_index = l_cat.posting_index
                            AND l_cash.id           <> l_cat.id
        JOIN accounts a_cash ON a_cash.id = l_cash.account_id
        JOIN txn_headers h ON h.id = l_cat.header_id
        WHERE h.action IS NOT NULL
          AND a_cat.account_type    = 'category'
          AND a_cat.category_kind   = 'expense'
          AND a_cash.account_type   = 'investment'
          AND a_cash.holdings_account_id IS NOT NULL
    )
    UPDATE txn_legs l
    SET posting_role = 'fee'
    FROM fee_postings fp
    WHERE l.header_id     = fp.header_id
      AND l.posting_index = fp.posting_index
      AND l.posting_role IS NULL;
    GET DIAGNOSTICS v_fee = ROW_COUNT;

    WITH inc_postings AS (
        SELECT DISTINCT l_cat.header_id, l_cat.posting_index
        FROM txn_legs l_cat
        JOIN accounts a_cat ON a_cat.id = l_cat.account_id
        JOIN txn_legs l_cash ON l_cash.header_id     = l_cat.header_id
                            AND l_cash.posting_index = l_cat.posting_index
                            AND l_cash.id           <> l_cat.id
        JOIN accounts a_cash ON a_cash.id = l_cash.account_id
        JOIN txn_headers h ON h.id = l_cat.header_id
        WHERE h.action IS NOT NULL
          AND a_cat.account_type    = 'category'
          AND a_cat.category_kind   = 'income'
          AND a_cash.account_type   = 'investment'
          AND a_cash.holdings_account_id IS NOT NULL
    )
    UPDATE txn_legs l
    SET posting_role = 'income'
    FROM inc_postings ip
    WHERE l.header_id     = ip.header_id
      AND l.posting_index = ip.posting_index
      AND l.posting_role IS NULL;
    GET DIAGNOSTICS v_income = ROW_COUNT;

    WITH xfr_postings AS (
        SELECT DISTINCT l1.header_id, l1.posting_index
        FROM txn_legs l1
        JOIN accounts a1 ON a1.id = l1.account_id
        JOIN txn_legs l2 ON l2.header_id     = l1.header_id
                        AND l2.posting_index = l1.posting_index
                        AND l2.id           <> l1.id
        JOIN accounts a2 ON a2.id = l2.account_id
        JOIN txn_headers h ON h.id = l1.header_id
        WHERE h.action IS NOT NULL
          AND a1.account_type IN ('bank', 'credit_card', 'investment')
          AND a2.account_type IN ('bank', 'credit_card', 'investment')
    )
    UPDATE txn_legs l
    SET posting_role = 'transfer'
    FROM xfr_postings xp
    WHERE l.header_id     = xp.header_id
      AND l.posting_index = xp.posting_index
      AND l.posting_role IS NULL;
    GET DIAGNOSTICS v_transfer = ROW_COUNT;

    RAISE NOTICE 'Migration 057 re-backfill: security=% fee=% income=% transfer=%',
        v_security, v_fee, v_income, v_transfer;
END;
$$;


-- -----------------------------------------------------------------------------
-- Part 2: Verify zero NULL-investment-leg rows remain. Fails loudly if
-- any leg in an investment header is still without a role — the trigger
-- can't be armed against dirty data.
-- -----------------------------------------------------------------------------

DO $$
DECLARE
    v_bad INTEGER;
    v_sample RECORD;
BEGIN
    SELECT COUNT(*) INTO v_bad
    FROM txn_legs l
    JOIN txn_headers h ON h.id = l.header_id
    WHERE h.action IS NOT NULL
      AND l.posting_role IS NULL;

    IF v_bad > 0 THEN
        SELECT l.id, l.header_id, h.action, l.account_id
        INTO v_sample
        FROM txn_legs l
        JOIN txn_headers h ON h.id = l.header_id
        WHERE h.action IS NOT NULL AND l.posting_role IS NULL
        LIMIT 1;

        RAISE EXCEPTION
            'Migration 057: % investment legs still have NULL posting_role after re-backfill. '
            'Sample: leg=%, header=%, action=%, account=%. Inspect the heuristic before arming the trigger.',
            v_bad, v_sample.id, v_sample.header_id, v_sample.action, v_sample.account_id;
    END IF;

    -- Symmetric check: legs with posting_role set on non-investment headers
    -- are also dirty data (shouldn't happen but the trigger will block
    -- future cases).
    SELECT COUNT(*) INTO v_bad
    FROM txn_legs l
    JOIN txn_headers h ON h.id = l.header_id
    WHERE h.action IS NULL
      AND l.posting_role IS NOT NULL;

    IF v_bad > 0 THEN
        RAISE EXCEPTION
            'Migration 057: % non-investment legs have non-NULL posting_role. '
            'Clean these before arming the trigger.', v_bad;
    END IF;
END;
$$;


-- -----------------------------------------------------------------------------
-- Part 3: Trigger — enforce posting_role IS NOT NULL ⇔ header.action IS NOT NULL.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_validate_posting_role()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_action TEXT;
BEGIN
    SELECT action INTO v_action FROM txn_headers WHERE id = NEW.header_id;

    IF v_action IS NOT NULL AND NEW.posting_role IS NULL THEN
        RAISE EXCEPTION
            'posting_role required on legs of investment headers '
            '(header_id=%, action=%, account_id=%, posting_index=%)',
            NEW.header_id, v_action, NEW.account_id, NEW.posting_index;
    END IF;

    IF v_action IS NULL AND NEW.posting_role IS NOT NULL THEN
        RAISE EXCEPTION
            'posting_role must be NULL on legs of non-investment headers '
            '(header_id=%, account_id=%, posting_role=%)',
            NEW.header_id, NEW.account_id, NEW.posting_role;
    END IF;

    RETURN NEW;
END;
$$;

COMMENT ON FUNCTION fn_validate_posting_role() IS
    'Enforces posting_role IS NOT NULL ⇔ txn_headers.action IS NOT NULL. '
    'Caught at write time on every INSERT/UPDATE of txn_legs. Symmetric: '
    'investment legs MUST declare a role, non-investment legs MUST NOT.';

CREATE TRIGGER trg_validate_posting_role
BEFORE INSERT OR UPDATE ON txn_legs
FOR EACH ROW
EXECUTE FUNCTION fn_validate_posting_role();


-- -----------------------------------------------------------------------------
-- Part 4: Rebuild `resolved_transactions` to expose posting_role.
--
-- The leg's OWN posting_role is the source of truth — both legs of a
-- posting share the same value, so the leg-side projection works
-- without needing to coalesce from the counterparty. Non-investment
-- legs surface as NULL.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE VIEW resolved_transactions AS
SELECT l.id,
    l.account_id,
    COALESCE(o.payee, h.payee) AS payee,
    COALESCE(lo.leg_memo, l.leg_memo, o.memo, h.memo) AS memo,
    COALESCE(lo.amount, l.amount) AS amount,
    COALESCE(o.posted_at, h.posted_at) AS posted_at,
    COALESCE(o.transacted_at, h.transacted_at) AS transacted_at,
    h.status,
    COALESCE(o.is_hidden, h.is_hidden, false) AS is_hidden,
    o.header_id IS NOT NULL OR lo.leg_id IS NOT NULL AS has_overrides,
    l.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    h.action AS investment_action,
    h.external_id,
    l.created_at,
    COALESCE(o.check_number, h.check_number) AS check_number,
    other.id AS counterparty_id,
        CASE
            WHEN (EXISTS ( SELECT 1
               FROM txn_legs g
              WHERE g.header_id = h.id AND g.posting_index > 0)) THEN h.id
            ELSE NULL::uuid
        END AS txn_group_id,
    l.posting_index AS leg_index,
    other.account_id AS counterparty_account_id,
    account_path(other.account_id) AS counterparty_account_name,
    ca.account_type AS counterparty_account_type,
    COALESCE(ARRAY( SELECT tg.name
           FROM txn_header_tags tt
             JOIN tags tg ON tg.id = tt.tag_id
          WHERE tt.header_id = h.id
          ORDER BY tg.name), ARRAY[]::text[]) AS tags,
    h.id AS header_id,
    h.cleared_at,
    h.cleared_by_user_id,
    COALESCE(lo.leg_memo, l.leg_memo) AS leg_memo,
    COALESCE(o.memo, h.memo) AS header_memo,
    h.online_match_fitid,
    h.online_match_fi_id,
    h.online_match_status,
    h.online_match_type,
    h.online_match_orig_id,
    h.needs_review,
    COALESCE(l.security_id, other.security_id) AS security_id,
    s.ticker AS security_ticker,
    s.name AS security_name,
    COALESCE(l.quantity, other.quantity) AS quantity,
    COALESCE(l.unit_price, other.unit_price) AS unit_price,
    l.posting_role
   FROM txn_legs l
     JOIN txn_headers h ON h.id = l.header_id
     LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
     LEFT JOIN txn_legs other ON other.header_id = l.header_id AND other.posting_index = l.posting_index AND other.id <> l.id
     LEFT JOIN accounts ca ON ca.id = other.account_id
     LEFT JOIN securities s ON s.id = COALESCE(l.security_id, other.security_id);

-- Re-assert security_invoker = true (originally set by migration 017,
-- re-asserted by migrations 018 / 023). `CREATE OR REPLACE VIEW`
-- preserves data but does NOT preserve view-level settings; without
-- this line, RLS policies on the underlying tables evaluate against
-- the view OWNER (typically coffer_service with BYPASSRLS) rather than
-- the caller — bypassing per-user row visibility. The RLS integration
-- test `Transactions_inherit_account_policy_via_FK_chain` catches the
-- regression if this line is dropped in a future view rebuild.
ALTER VIEW resolved_transactions SET (security_invoker = true);
