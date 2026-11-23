-- =============================================================================
-- 274 — a trade price is in the currency of the brokerage that made the trade
-- =============================================================================
--
-- WHY
-- ---
-- security_price_upsert_from_trade (migration 177) stored every trade-derived price
-- with currency_code 'USD', whatever the currency of the brokerage the trade was made
-- in, and the importer's seed did the same. Nothing converts currencies (FU-106), so a
-- buy in a brokerage kept in another currency stored its price as a dollar figure, and
-- every reader that names a security's current price in its price row's currency (the
-- security's detail, the securities catalog, the unbacked-price advisory: ADR-0084's
-- "a security's current figures name their currency" amendment) printed it in dollars.
-- The checker (fn_trade_price_check) compared the price and never the currency, so it
-- could not see such a row.
--
-- One rule (ADR-0084 D2, 2026-11-04 amendment): a trade price is in the currency of the
-- brokerage that made the trade. The rule picks one execution per (security, UTC day),
-- last by seq, then largest by value, then leg id, so the currency is determined even
-- when one security trades in two brokerages on one day. The execution's line sits on
-- its brokerage's holdings account (ADR-0019 Rule 4); the brokerage is the account that
-- names that holdings account (accounts.holdings_account_id), and its currency_code is
-- the one read. A line on an account no brokerage names reads that account's own. When
-- more than one brokerage names one holdings account and they disagree, the first code in
-- ordinal order is read, as the holdings-currency check names the one it expects, so the
-- two agree.
--
-- The holdings account's own currency is not read. It is kept equal to its brokerage's
-- (ADR-0019 Rule 4, 2026-11-04 amendment), and the consistency check's holdings_currency
-- projection reports one that differs, but a trade price is written and checked in the
-- brokerage's currency whether or not that projection has been repaired: the currency the
-- account editor shows, and the one that repair sets the holdings account to.
--
-- WHAT CHANGES
-- ------------
--   * fn_trade_currency_accounts (new): the accounts whose currency a trade on a line's
--     account is priced in: every brokerage naming the account as its holdings account,
--     or the account itself when none does.
--   * fn_trade_currency (new): the one of those whose currency_code the price takes, the
--     first by code in ordinal order, then by id.
--   * fn_trade_day_executions (new): a security's executions on a UTC day, with what the
--     rule orders them by. Its filter is fn_trade_price_for_day's of migration 273.
--   * fn_trade_execution_for_day (new): the rule, returning the picked execution's price,
--     the currency fn_trade_currency names for its line's account, and the account whose
--     currency that is. Its order is fn_trade_price_for_day's of migration 273.
--   * fn_trade_price_accounts (new): every account whose currency the rule can read for a
--     set of (security, day) pairs, whichever execution of each day it picks: what the
--     writers lock (LOCK ORDER below). It reads the same executions as the rule.
--   * fn_lock_trade_price_accounts (new): locks given accounts in id order, FOR NO KEY
--     UPDATE or FOR SHARE.
--   * fn_lock_securities (new): locks given securities FOR UPDATE in id order, as
--     migration 264's fn_lock_tags and fn_lock_accounts lock theirs: what merge_securities
--     takes on the source before it reads which lines are the source's.
--   * fn_trade_price_for_day is dropped. Its one caller, fn_trade_price_reseed, reads
--     fn_trade_execution_for_day below, and a function nothing calls is a second
--     statement of the rule that nothing keeps in step with the first (as migration 273
--     dropped fn_price_is_unbacked). The one-shot deletes of migrations 236 to 238 that
--     called it run before this one.
--   * security_price_upsert_from_trade: takes the currency it writes, a fifth argument
--     (uuid, uuid, date, numeric, text). The four-argument form is dropped by its exact
--     signature; nothing else calls it once the reseed below passes a currency. The
--     rank gate is unchanged.
--   * fn_trade_price_reseed: writes the price and the currency fn_trade_execution_for_day
--     names, read after it has locked FOR SHARE every account fn_trade_price_accounts
--     names for the day. The pick and that set are read in one statement, which is run
--     again until every account in the set was locked before it began, so the currency
--     written is the one committed when the lock was granted, whichever execution the
--     read picks. Same signature and return type.
--   * fn_trade_price_check: adds expected_currency (fn_trade_currency's for the picked
--     execution's line) and stored_currency (the stored row's), and reports reason
--     'value' when a stored import, simplefin or trade row holds another currency, as it
--     does for another price. A fetch or manual price owns its day and is not compared,
--     as before. The output columns change, so it is dropped and created again, and its
--     grants are re-issued.
--
-- fn_leg_prices_a_day and fn_unbacked_price_securities read no currency and are
-- unchanged. fn_unbacked_price_dates reads none either; its body, unchanged but for a
-- comment, and its COMMENT are re-issued so they name fn_trade_execution_for_day as the
-- rule they restate, not the dropped function.
--
-- LOCK ORDER
-- ----------
-- Every path that writes a trade price into a committed ledger, or a currency the
-- trade-price rule reads, takes its locks in one order: the API's writers, native and MCP.
-- The importer's seed (TradePriceSeedStep) is outside it: it writes only into the ledger
-- its own transaction creates, which no other session can see until that transaction
-- commits, so no other path can run against it.
--
--   Before 0. The ledger write lock (fn_ledger_write_lock, migration 275), as the
--      transaction's first statement, by every path in this order that can recompute the
--      ledger's balances or holdings (merge_securities among them), by a currency edit, by
--      a re-derivation that opens its own transaction, and by every consistency repair, the
--      trade-price and holdings-currency repairs included. Two transactions that take it
--      are never past it at once in one ledger, so no wait between them at the steps below
--      closes a cycle. A re-derivation run in a caller's transaction that recomputes
--      neither balances nor holdings takes none, and steps 1 to 3 keep it from a cycle.
--   0. merge_securities only: the source security's row, FOR UPDATE (fn_lock_securities),
--      before it reads which lines are the source's. A line, holdings row or price row
--      another request adds for the source takes KEY SHARE on that row, which FOR UPDATE
--      excludes: one in flight is waited for and then read, and one that starts later waits
--      for the merge, so no line moves with the merge's others unread.
--   1. The accounts whose currency the rule reads for the (security, day) pairs the path
--      derives (fn_trade_price_accounts: brokerages, and accounts no brokerage names),
--      with the account a currency edit writes, in one statement, in id order
--      (fn_lock_trade_price_accounts). A writer that derives several days locks the
--      accounts of all of them in that one statement (TradePriceRecomputeService), not
--      day by day. The mode is the path's:
--        - FOR SHARE: a re-derivation, which every writer of a trade's day runs through
--          the reseed (a trade's save; a hide, an unhide or a merge; the bulk delete and
--          restore; merge_securities; a currency edit's own re-derivation), and the
--          holdings-currency repair, which reads its brokerages' currencies;
--        - FOR NO KEY UPDATE: a brokerage's currency edit, and the trade-price repair,
--          which writes the figures the check derived and so must not read past a
--          re-derivation in flight.
--   2. A brokerage's holdings account row, which a currency edit and the
--      holdings-currency repair update once its brokerage is locked. No trade-price path
--      locks a holdings account: the rule never reads its currency.
--   3. security_prices rows, through the rank-gated upsert, in (ledger, security, day)
--      order (TradePriceRecomputeService.InWriteOrder) by every path that writes more than
--      one: the re-derivation and the trade-price repair.
--
-- The path then reads what it writes from, in a statement that starts after the locks are
-- granted, and reads the set of accounts again: an account that joined it in between (an
-- execution of the day another request committed on another brokerage) is locked and the
-- read repeated.
--
-- FOR SHARE conflicts with FOR NO KEY UPDATE and with an UPDATE of the account, and not
-- with FOR SHARE, so a re-derivation waits for a currency edit or a trade-price repair in
-- flight, they wait for it, and re-derivations do not wait for each other at step 1. The
-- holdings-currency repair and a re-derivation take no lock the other holds, so neither
-- waits for the other. Neither mode conflicts with the KEY SHARE a new line's foreign key
-- takes on its account, so no lock here waits for a trade's insert, and no insert waits
-- for one. Each upsert holds its price row until its transaction ends, so two
-- re-derivations of the same days meet at step 3, and the one order keeps them from each
-- holding a row the other waits for.
--
-- The step before 0 supersedes what this paragraph says about not waiting, for the paths
-- that take the ledger write lock: two of them never run at once in one ledger, so two
-- re-derivations of one ledger that each take it, or the holdings-currency repair and such
-- a re-derivation, take turns there before either reaches step 1. A re-derivation run in
-- a caller's transaction that recomputes neither balances nor holdings takes none; for it
-- this paragraph stands, and steps 1 to 3 keep it from a cycle.
--
-- Each path takes all its account locks in one id-ordered statement before any price row,
-- and its price rows in one order, so that no two paths hold an account or a price row the
-- other waits for. With the ledger write lock first, the deadlocks and narrow timing
-- windows the steps from 0 on would leave between two paths of one ledger (a security
-- merge racing a re-dated trade, two saves re-deriving the same day, either of which could
-- leave a trade price stale) cannot form. A transaction outside this order that row-locks
-- the same accounts without the ledger write lock can still deadlock with one of them.
-- Postgres then cancels one request, which writes nothing, and a retry succeeds. That is an
-- accepted limit.
--
-- EXISTING INSTALLS
-- -----------------
-- No row is written. A trade price stored in dollars on a day a trade in a brokerage kept
-- in another currency prices now disagrees with the rule: the consistency check reports
-- it once, as a trade-price value mismatch naming both currencies, and the trade-price
-- repair writes the brokerage's currency through the rank-gated writer. A holdings
-- account left in a currency other than its brokerage's by an earlier edit is reported by
-- the check's holdings-currency projection, whose repair sets it to the brokerage's and
-- touches no price.
-- =============================================================================

BEGIN;

CREATE FUNCTION fn_trade_currency_accounts(p_account_id uuid)
RETURNS TABLE (account_id uuid, currency_code text)
LANGUAGE sql
STABLE
AS $$
    -- The brokerages that name the account as their holdings account (ADR-0019 Rule 4).
    SELECT b.id, b.currency_code
      FROM accounts b
     WHERE b.holdings_account_id = p_account_id
    UNION ALL
    -- A line on an account no brokerage names: that account.
    SELECT a.id, a.currency_code
      FROM accounts a
     WHERE a.id = p_account_id
       AND NOT EXISTS (SELECT 1 FROM accounts n WHERE n.holdings_account_id = p_account_id);
$$;

COMMENT ON FUNCTION fn_trade_currency_accounts(uuid) IS
    'Mig 274. The accounts whose currency a trade on a line of this account is priced in '
    '(ADR-0084 D2, 2026-11-04 amendment): every brokerage naming it as its holdings '
    'account, or the account itself when none does. A holdings account''s own currency is '
    'never among them.';

GRANT EXECUTE ON FUNCTION fn_trade_currency_accounts(uuid) TO coffer_app, coffer_service;

CREATE FUNCTION fn_trade_currency(p_account_id uuid)
RETURNS TABLE (account_id uuid, currency_code text)
LANGUAGE sql
STABLE
AS $$
    -- One brokerage, or brokerages that agree, name one code. Brokerages that disagree
    -- name the first in ordinal order, as the holdings-currency check expects it
    -- (HoldingsAccountCurrency), so the two agree; the id orders the rest.
    SELECT c.account_id, c.currency_code
      FROM fn_trade_currency_accounts(p_account_id) c
     ORDER BY c.currency_code COLLATE "C", c.account_id
     LIMIT 1;
$$;

COMMENT ON FUNCTION fn_trade_currency(uuid) IS
    'Mig 274. The currency a trade on a line of this account is priced in, and the account '
    'it is read from: of fn_trade_currency_accounts, the first by code in ordinal order, '
    'then by id. A trade price is in the currency of the brokerage that made the trade '
    '(ADR-0084 D2, 2026-11-04 amendment).';

GRANT EXECUTE ON FUNCTION fn_trade_currency(uuid) TO coffer_app, coffer_service;

CREATE FUNCTION fn_trade_day_executions(
    p_ledger_id uuid, p_security_id uuid, p_day date)
RETURNS TABLE (leg_id uuid, account_id uuid, price numeric, seq bigint, value numeric)
LANGUAGE sql
STABLE
AS $$
    SELECT l.id, l.account_id, round(l.unit_price, 4), h.seq, abs(l.amount)
    FROM txn_legs l
    JOIN live_txn_headers h ON h.id = l.header_id
    WHERE l.ledger_id    = p_ledger_id
      AND l.security_id  = p_security_id
      AND (h.posted_at AT TIME ZONE 'UTC')::date = p_day
      -- A hidden or merged-away row is no trade, as for fn_last_trade_price.
      AND h.is_hidden    = FALSE
      AND h.is_merged_into IS NULL
      AND fn_leg_prices_a_day(l.quantity, l.unit_price, h.action,
                              h.provider_raw_payload->>'qif_invst_action');
$$;

COMMENT ON FUNCTION fn_trade_day_executions(uuid, uuid, date) IS
    'Mig 274. The executions of a security on a UTC day that the trade-price rule picks '
    'from (fn_leg_prices_a_day; hidden, merged and template headers are not trades, as '
    'for fn_last_trade_price), with what the pick orders by. Read by the rule '
    '(fn_trade_execution_for_day) and by the accounts it can read (fn_trade_price_accounts), '
    'so the two cannot disagree about which lines are trades.';

GRANT EXECUTE ON FUNCTION fn_trade_day_executions(uuid, uuid, date)
    TO coffer_app, coffer_service;

CREATE FUNCTION fn_trade_execution_for_day(
    p_ledger_id uuid, p_security_id uuid, p_day date)
RETURNS TABLE (price numeric, currency_code text, account_id uuid)
LANGUAGE sql
STABLE
AS $$
    SELECT x.price, c.currency_code, c.account_id
    FROM (
        SELECT d.price, d.account_id
        FROM fn_trade_day_executions(p_ledger_id, p_security_id, p_day) d
        ORDER BY d.seq DESC, d.value DESC, d.leg_id DESC
        LIMIT 1
    ) x
    -- The currency of the brokerage that made the trade, not of the holdings account
    -- its line sits on.
    CROSS JOIN LATERAL fn_trade_currency(x.account_id) c;
$$;

COMMENT ON FUNCTION fn_trade_execution_for_day(uuid, uuid, date) IS
    'Mig 274. The single definition of a day''s trade-derived price and its currency: '
    'among the day''s executions of the security (fn_trade_day_executions), the last by '
    'seq, the largest by value within a header, leg id as a total order; its unit price '
    'at 4dp, the currency of the brokerage that made the trade (fn_trade_currency), and '
    'the account that currency is read from. No row when no execution prices the day.';

GRANT EXECUTE ON FUNCTION fn_trade_execution_for_day(uuid, uuid, date)
    TO coffer_app, coffer_service;

CREATE FUNCTION fn_trade_price_accounts(
    p_ledger_id uuid, p_security_ids uuid[], p_days date[])
RETURNS TABLE (account_id uuid)
LANGUAGE sql
STABLE
AS $$
    -- Whichever execution of a day the rule picks, the accounts it can read the currency
    -- of: those fn_trade_currency chooses among, for every execution of the day.
    SELECT DISTINCT c.account_id
    FROM unnest(p_security_ids, p_days) AS k(security_id, on_day)
    CROSS JOIN LATERAL fn_trade_day_executions(p_ledger_id, k.security_id, k.on_day) d
    CROSS JOIN LATERAL fn_trade_currency_accounts(d.account_id) c;
$$;

COMMENT ON FUNCTION fn_trade_price_accounts(uuid, uuid[], date[]) IS
    'Mig 274. Every account whose currency the trade-price rule can read for the given '
    '(security, UTC day) pairs, the arrays read in step: for each execution of each day '
    '(fn_trade_day_executions), the accounts fn_trade_currency chooses among. What every '
    'writer of a trade price into a committed ledger, or of a currency the rule reads, '
    'locks before it reads (fn_lock_trade_price_accounts).';

GRANT EXECUTE ON FUNCTION fn_trade_price_accounts(uuid, uuid[], date[])
    TO coffer_app, coffer_service;

CREATE FUNCTION fn_lock_trade_price_accounts(p_account_ids uuid[], p_for_write boolean)
RETURNS TABLE (locked integer)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION
            'fn_lock_trade_price_accounts must run in a READ COMMITTED transaction, not %: '
            'the writer reads the currencies after the lock, which only READ COMMITTED sees '
            'as committed', current_setting('transaction_isolation')
            USING ERRCODE = 'invalid_transaction_state';
    END IF;
    IF p_for_write THEN
        RETURN QUERY
        WITH l AS (
            SELECT a.id FROM accounts a
             WHERE a.id = ANY(p_account_ids)
             ORDER BY a.id
               FOR NO KEY UPDATE
        )
        SELECT count(*)::integer FROM l;
    ELSE
        RETURN QUERY
        WITH l AS (
            SELECT a.id FROM accounts a
             WHERE a.id = ANY(p_account_ids)
             ORDER BY a.id
               FOR SHARE
        )
        SELECT count(*)::integer FROM l;
    END IF;
END;
$$;

COMMENT ON FUNCTION fn_lock_trade_price_accounts(uuid[], boolean) IS
    'Mig 274. Locks the given accounts in id order until the caller''s transaction ends: '
    'FOR NO KEY UPDATE for a writer that must exclude trade-price re-derivations in flight '
    '(a currency edit, the trade-price repair), FOR SHARE for a re-derivation or a reader '
    'of a brokerage''s currency (the holdings-currency repair). Neither excludes the KEY '
    'SHARE a new line''s foreign key takes. Returns how many it locked. Refuses any '
    'isolation level but READ COMMITTED. The lock order is in migration 274''s header.';

GRANT EXECUTE ON FUNCTION fn_lock_trade_price_accounts(uuid[], boolean)
    TO coffer_app, coffer_service;

-- merge_securities reads which lines are the source's, repoints every line of it, and
-- re-derives the days and holdings of the lines it read. A line another request adds to
-- the source in between would move with the others and leave its day and its holdings
-- with the keeper un-derived. A new line, holdings row or price row takes a KEY SHARE lock
-- on the security it references, which FOR UPDATE excludes, so locking the source first
-- closes that window, as fn_lock_tags and fn_lock_accounts (migration 264) close it for a
-- tag or category merge. In id order and READ COMMITTED only, as those are.
CREATE FUNCTION fn_lock_securities(p_security_ids uuid[])
RETURNS TABLE (locked integer)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION
            'fn_lock_securities must run in a READ COMMITTED transaction, not %: the writer '
            'reads the security''s lines after the lock, which only READ COMMITTED sees as '
            'committed', current_setting('transaction_isolation')
            USING ERRCODE = 'invalid_transaction_state';
    END IF;
    RETURN QUERY
    WITH l AS (
        SELECT s.id FROM securities s
         WHERE s.id = ANY(p_security_ids)
         ORDER BY s.id
           FOR UPDATE
    )
    SELECT count(*)::integer FROM l;
END;
$$;

COMMENT ON FUNCTION fn_lock_securities(uuid[]) IS
    'Mig 274. Locks the given securities FOR UPDATE in id order until the caller''s '
    'transaction ends, so no line, holdings row or price row referencing them is added '
    'until it does. Returns how many it locked. Refuses any isolation level but READ '
    'COMMITTED. The lock order is in migration 274''s header.';

GRANT EXECUTE ON FUNCTION fn_lock_securities(uuid[]) TO coffer_app, coffer_service;

-- The four-argument form wrote 'USD' whatever the trade's currency. Dropped by its exact
-- signature, without IF EXISTS: a signature that matched nothing would drop nothing.
DROP FUNCTION security_price_upsert_from_trade(uuid, uuid, date, numeric);

-- The OUT column stays `upserted_security_id`: a RETURNS TABLE column named
-- `security_id` collides with the table's column in ON CONFLICT (migration 177).
CREATE FUNCTION security_price_upsert_from_trade(
    p_ledger_id     uuid,
    p_security_id   uuid,
    p_day           date,
    p_price         numeric,
    p_currency_code text
)
RETURNS TABLE (upserted_security_id uuid)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    -- A priceless leg writes no price. Echo the input security_id so EF always has a
    -- typed projection row.
    IF p_price IS NULL OR p_price <= 0 THEN
        RETURN QUERY SELECT p_security_id;
        RETURN;
    END IF;

    -- A NULL currency fails the NOT NULL column rather than storing a default.
    INSERT INTO security_prices (id, security_id, ledger_id, price, currency_code, price_date, source)
    VALUES (gen_random_uuid(), p_security_id, p_ledger_id, p_price, p_currency_code, p_day, 'trade')
    ON CONFLICT (security_id, price_date) DO UPDATE
        SET price         = EXCLUDED.price,
            source        = 'trade',
            currency_code = EXCLUDED.currency_code
        WHERE security_prices.source IN ('import', 'simplefin', 'trade');

    RETURN QUERY SELECT p_security_id;
END;
$$;

COMMENT ON FUNCTION security_price_upsert_from_trade(uuid, uuid, date, numeric, text) IS
    'Mig 274. The rank-gated write of a trade-source price for a (security, UTC day), in '
    'the currency given: inserts on an empty day, overwrites an import, simplefin or '
    'trade row, never a fetch or manual one. A NULL or non-positive price writes nothing.';

GRANT EXECUTE ON FUNCTION security_price_upsert_from_trade(uuid, uuid, date, numeric, text)
    TO coffer_app, coffer_service;

CREATE OR REPLACE FUNCTION fn_trade_price_reseed(
    p_ledger_id uuid, p_security_id uuid, p_day date)
RETURNS TABLE (price numeric)
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    v_locked   uuid[] := '{}';
    v_accounts uuid[];
    v_price    numeric;
    v_currency text;
BEGIN
    -- The day's pick and every account whose currency the rule can read for the day,
    -- read in one statement, so from one snapshot. Until every one of those accounts was
    -- locked before the statement began, they are locked FOR SHARE, in id order, and the
    -- statement is run again: a currency edit or a trade-price repair in flight is waited
    -- for, and one that starts later waits for this transaction (the lock order in the
    -- header). The set grows between two reads only when another request commits an
    -- execution of the day on another brokerage in between.
    LOOP
        SELECT e.price, e.currency_code,
               ARRAY(SELECT t.account_id
                       FROM fn_trade_price_accounts(p_ledger_id, ARRAY[p_security_id], ARRAY[p_day]) t)
          INTO v_price, v_currency, v_accounts
          FROM (SELECT 1) AS one
          LEFT JOIN fn_trade_execution_for_day(p_ledger_id, p_security_id, p_day) e ON TRUE;
        EXIT WHEN v_accounts <@ v_locked;
        PERFORM 1 FROM fn_lock_trade_price_accounts(v_accounts, FALSE);
        v_locked := v_locked || v_accounts;
    END LOOP;
    IF v_price IS NULL THEN
        -- No execution prices the day. ADR-0084 D4 keeps the existing row. One row,
        -- NULL price, so the caller can tell "nothing to seed" from "did not run".
        RETURN QUERY SELECT NULL::numeric;
        RETURN;
    END IF;
    PERFORM security_price_upsert_from_trade(
        p_ledger_id, p_security_id, p_day, v_price, v_currency);
    RETURN QUERY SELECT v_price;
END;
$$;

COMMENT ON FUNCTION fn_trade_price_reseed(uuid, uuid, date) IS
    'Mig 274. Derive the day''s trade price and its currency by the one rule '
    '(fn_trade_execution_for_day) and rank-gate them into security_prices, read after '
    'every account whose currency the rule can read for the day (fn_trade_price_accounts) '
    'is locked FOR SHARE, so a currency edit or a trade-price repair in flight is waited '
    'for. Returns the price written, or NULL when no execution prices the day (the '
    'existing row is left alone, ADR-0084 D4). The read and the accounts it can read are '
    'one statement, run again until those accounts were all locked before it began: under '
    'READ COMMITTED, as every writer runs, its fresh snapshot sees what the lock waited for.';

-- The rule's price alone (migration 234). Nothing calls it now that the reseed above reads
-- fn_trade_execution_for_day. Dropped by its exact signature, without IF EXISTS, as the
-- four-argument writer is.
DROP FUNCTION fn_trade_price_for_day(uuid, uuid, date);

-- Migration 273's body, unchanged but for the comment naming the rule it restates.
CREATE OR REPLACE FUNCTION fn_unbacked_price_dates(
    p_ledger_id uuid, p_security_id uuid)
RETURNS TABLE (price_date date)
LANGUAGE sql
STABLE
AS $$
    WITH derived AS (
        SELECT DISTINCT (h.posted_at AT TIME ZONE 'UTC')::date AS d
        FROM txn_legs l
        JOIN live_txn_headers h ON h.id = l.header_id
        WHERE l.ledger_id   = p_ledger_id
          AND l.security_id = p_security_id
          -- The rule's own filter (fn_trade_execution_for_day), in one pass.
          AND h.is_hidden = FALSE
          AND h.is_merged_into IS NULL
          AND fn_leg_prices_a_day(l.quantity, l.unit_price, h.action,
                                  h.provider_raw_payload->>'qif_invst_action')
    )
    SELECT p.price_date
    FROM security_prices p
    WHERE p.ledger_id   = p_ledger_id
      AND p.security_id = p_security_id
      AND p.source = 'trade'
      AND NOT EXISTS (SELECT 1 FROM derived d WHERE d.d = p.price_date);
$$;

COMMENT ON FUNCTION fn_unbacked_price_dates(uuid, uuid) IS
    'Mig 274. Dates on which this security has a trade-source price that no trade '
    'currently derives (fn_trade_execution_for_day''s rule: executions only; hidden, '
    'merged and template headers are not trades). One pass over the security''s legs: a '
    'per-row test rescans them once per price row, a cost that grows with the length '
    'of the price history. '
    'Advisory: such a price may still be correct (ADR-0084 D4).';

-- The output columns change, which CREATE OR REPLACE cannot do (as in migration 233).
DROP FUNCTION fn_trade_price_check(uuid);

CREATE FUNCTION fn_trade_price_check(p_ledger_id uuid)
RETURNS TABLE (
    security_id       uuid,
    price_date        date,
    expected_price    numeric,
    stored_price      numeric,
    stored_source     text,
    reason            text,
    account_id        uuid,
    header_id         uuid,
    expected_currency text,
    stored_currency   text
)
LANGUAGE sql
STABLE
AS $$
    WITH expected AS (
        SELECT DISTINCT ON (l.security_id, (h.posted_at AT TIME ZONE 'UTC')::date)
               l.security_id,
               (h.posted_at AT TIME ZONE 'UTC')::date AS price_date,
               round(l.unit_price, 4)                 AS price,
               l.account_id,
               h.id                                   AS header_id
        FROM txn_legs l
        JOIN live_txn_headers h ON h.id = l.header_id
        WHERE l.ledger_id = p_ledger_id
          AND l.security_id IS NOT NULL
          -- The rule's own filter (fn_trade_execution_for_day), stated set-wide.
          AND h.is_hidden = FALSE
          AND h.is_merged_into IS NULL
          AND fn_leg_prices_a_day(l.quantity, l.unit_price, h.action,
                                  h.provider_raw_payload->>'qif_invst_action')
        ORDER BY l.security_id,
                 (h.posted_at AT TIME ZONE 'UTC')::date,
                 h.seq DESC, abs(l.amount) DESC, l.id DESC
    )
    SELECT e.security_id,
           e.price_date,
           e.price,
           p.price,
           p.source,
           CASE
               WHEN p.security_id IS NULL THEN 'missing'
               WHEN p.source IN ('import', 'simplefin', 'trade')
                    AND (p.price <> e.price OR p.currency_code <> a.currency_code) THEN 'value'
               ELSE NULL
           END AS reason,
           e.account_id,
           e.header_id,
           a.currency_code,
           p.currency_code
    FROM expected e
    -- The currency of the brokerage that made the picked trade (fn_trade_currency, the
    -- rule's), joined after the pick: one row per day.
    CROSS JOIN LATERAL fn_trade_currency(e.account_id) a
    LEFT JOIN security_prices p
           ON p.security_id = e.security_id
          AND p.price_date  = e.price_date;
$$;

COMMENT ON FUNCTION fn_trade_price_check(uuid) IS
    'Mig 274. Every (security, UTC day) the trade-price rule prices '
    '(fn_trade_execution_for_day: executions only; hidden, merged and template headers '
    'are not trades), with the price it derives, the currency of the brokerage that made '
    'the picked trade (fn_trade_currency), and the stored row''s price and currency. '
    'reason NULL = agrees, or a fetch or manual price owns the day; missing / value = '
    'repairable, value covering another price or another currency on an import, '
    'simplefin or trade row. A stored trade price no execution derives is not here: '
    'that is the unbacked-price advisory (ADR-0084 D6).';

GRANT EXECUTE ON FUNCTION fn_trade_price_check(uuid) TO coffer_app, coffer_service;

COMMIT;
