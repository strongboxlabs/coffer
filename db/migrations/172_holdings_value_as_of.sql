-- 172 — as-of valuation feeder for historical valuations (net-worth-over-time + TWR)
--
-- Track-2 "historical valuations" (docs/follow-ups.md): the point-in-time
-- feeder that BOTH net_worth_history AND true time-weighted return (ADR-0063
-- v2, ReturnsCalculator.Twr) depend on. Two read-only, side-effect-free
-- functions valued AS OF an arbitrary instant:
--
--   1. holdings_market_value_as_of — per (holdings-account, security) the
--      SPLIT-ADJUSTED quantity held at p_as_of, valued at the price as of that
--      instant. Quantity is a split-aware replay that mirrors
--      recompute_holdings_cost_basis's event stream (legs UNION splits, ordered
--      event_at, sort_class, leg_id) bounded to <= p_as_of — so as-of=now equals
--      the authoritative holdings.quantity. FIFO/cost-basis is NOT needed:
--      valuation is quantity x price. Price-as-of is a two-tier lookup:
--        feed  = latest security_prices close with price_date <= p_as_of, else
--        trade = latest txn_legs.unit_price on this (account, security) <= p_as_of
--                (a held security always has a buy <= T, so this almost always
--                 resolves — the buy price IS a real price observation).
--      priced_from records which tier valued each row ('feed'|'trade'|'none').
--
--   2. account_balance_as_of — the date-bounded twin of the mig-133
--      account_current_balances view: the register's own balance_after for the
--      last header with posted_at <= p_as_of, opening_balance as the fallback.
--
-- Both are SECURITY INVOKER (default), so the caller's RLS on the underlying
-- tables scopes every read to the bearer's ledgers; p_ledger_id keeps the plan
-- tight. Callers (net_worth_history, returns/TWR) sum + assemble in the repo
-- layer (which accounts contribute cash vs. holdings value is the caller's
-- concern, matching OverviewRepository — the holdings-sibling account is valued
-- via #1, never double-counted as cash via #2).

-- ---------------------------------------------------------------------------
-- 1. Split-adjusted holdings market value as of an instant.
-- ---------------------------------------------------------------------------
CREATE FUNCTION holdings_market_value_as_of(
    p_ledger_id   UUID,
    p_as_of       TIMESTAMPTZ,
    p_account_id  UUID DEFAULT NULL,
    p_security_id UUID DEFAULT NULL
)
RETURNS TABLE(
    account_id   UUID,
    security_id  UUID,
    quantity     NUMERIC,
    market_value NUMERIC,
    priced_from  TEXT
)
LANGUAGE plpgsql
STABLE
PARALLEL SAFE
AS $$
DECLARE
    v_pos        RECORD;
    v_event      RECORD;
    v_split      RECORD;
    v_qty        NUMERIC;
    v_price      NUMERIC;
    v_price_date DATE;
    v_obs_ts     TIMESTAMPTZ;
    v_factor     NUMERIC;
    v_from       TEXT;
BEGIN
    -- Every (holdings-account, security) that held ANY holdings-side leg by
    -- p_as_of — NOT the current holdings table, since a position fully sold
    -- since p_as_of must still be valued at a past instant.
    FOR v_pos IN
        SELECT DISTINCT l.account_id AS acct, l.security_id AS sec, l.ledger_id AS led
        FROM txn_legs l
        JOIN live_txn_headers h ON h.id = l.header_id
        WHERE l.security_id IS NOT NULL
          AND l.quantity    IS NOT NULL
          AND l.ledger_id   = p_ledger_id
          AND (p_account_id  IS NULL OR l.account_id  = p_account_id)
          AND (p_security_id IS NULL OR l.security_id = p_security_id)
          AND h.posted_at <= p_as_of
          AND h.is_hidden = FALSE
          AND h.is_merged_into IS NULL
    LOOP
        -- Split-adjusted quantity: replay this position's legs + splits in the
        -- same canonical order recompute_holdings_cost_basis uses (split before
        -- buy before sell on a shared instant), bounded to <= p_as_of.
        v_qty := 0;
        FOR v_event IN
            SELECT hd.posted_at AS event_at,
                   l.quantity   AS qty,
                   NULL::NUMERIC AS ratio,
                   CASE WHEN l.quantity > 0 THEN 1 ELSE 2 END AS sort_class,
                   l.id AS leg_id
            FROM txn_legs l
            JOIN live_txn_headers hd ON hd.id = l.header_id
            WHERE l.security_id = v_pos.sec
              AND l.account_id  = v_pos.acct
              AND l.quantity    IS NOT NULL
              AND hd.posted_at <= p_as_of
              AND hd.is_hidden = FALSE
              AND hd.is_merged_into IS NULL

            UNION ALL

            SELECT ss.split_at, NULL::NUMERIC, ss.ratio, 0 AS sort_class, NULL::UUID
            FROM security_splits ss
            WHERE ss.security_id = v_pos.sec
              AND ss.ledger_id   = v_pos.led
              AND ss.split_at   <= p_as_of

            ORDER BY event_at, sort_class, leg_id
        LOOP
            IF v_event.ratio IS NOT NULL THEN
                v_qty := v_qty * v_event.ratio;
            ELSE
                v_qty := v_qty + v_event.qty;
            END IF;
        END LOOP;

        -- Fully closed by p_as_of → not a holding at that instant; skip.
        CONTINUE WHEN v_qty = 0;

        -- Price-as-of. Tier 1: latest feed close on or before p_as_of.
        v_price := NULL; v_price_date := NULL; v_obs_ts := NULL;
        SELECT sp.price, sp.price_date INTO v_price, v_price_date
        FROM security_prices sp
        WHERE sp.ledger_id   = p_ledger_id
          AND sp.security_id = v_pos.sec
          AND sp.price_date <= p_as_of::date
        ORDER BY sp.price_date DESC
        LIMIT 1;

        IF v_price IS NOT NULL THEN
            v_from   := 'feed';
            -- A same-day split is already reflected in that day's close, so the
            -- observation boundary is the start of the day AFTER the feed date.
            v_obs_ts := (v_price_date + 1)::timestamptz;
        ELSE
            -- Tier 2: latest trade execution price on this (account, security).
            SELECT l.unit_price, h.posted_at INTO v_price, v_obs_ts
            FROM txn_legs l
            JOIN live_txn_headers h ON h.id = l.header_id
            WHERE l.security_id = v_pos.sec
              AND l.account_id  = v_pos.acct
              AND l.unit_price  IS NOT NULL
              AND h.posted_at  <= p_as_of
              AND h.is_hidden = FALSE
              AND h.is_merged_into IS NULL
            ORDER BY h.posted_at DESC, l.id DESC
            LIMIT 1;
            v_from := CASE WHEN v_price IS NOT NULL THEN 'trade' ELSE 'none' END;
        END IF;

        -- Back-adjust the observed per-share price to p_as_of's SPLIT BASIS.
        -- Quantity above is already split-adjusted to p_as_of; a price observed
        -- BEFORE a split falling on/before p_as_of is on the pre-split per-share
        -- basis, so a pre-observation share becomes (product of intervening
        -- ratios) shares by p_as_of and the per-share price divides by that
        -- product. Without this, split-adjusted qty x a raw price double-counts
        -- the split. No-op when no split falls between the observation and
        -- p_as_of (the dense-feed common case). Exact NUMERIC product.
        IF v_price IS NOT NULL AND v_obs_ts IS NOT NULL THEN
            v_factor := 1;
            FOR v_split IN
                SELECT ss.ratio
                FROM security_splits ss
                WHERE ss.security_id = v_pos.sec
                  AND ss.ledger_id   = v_pos.led
                  AND ss.split_at   >  v_obs_ts
                  AND ss.split_at   <= p_as_of
            LOOP
                v_factor := v_factor * v_split.ratio;
            END LOOP;
            IF v_factor > 0 AND v_factor <> 1 THEN
                -- ROUND bounds the NUMERIC scale — division produces an
                -- unbounded fractional scale that would overflow System.Decimal.
                v_price := ROUND(v_price / v_factor, 12);
            END IF;
        END IF;

        -- Bound scales so the NUMERICs fit System.Decimal: quantity ×/+ split
        -- ratios and qty × price otherwise carry an unbounded fractional scale
        -- (12 + 12 = 24 dp and up) that overflows the client-side decimal read.
        -- Share scale is 12dp (NUMERIC(25,12)); money is 4dp (NUMERIC(19,4)).
        account_id   := v_pos.acct;
        security_id  := v_pos.sec;
        quantity     := ROUND(v_qty, 12);
        market_value := ROUND(v_qty * COALESCE(v_price, 0), 4);
        priced_from  := v_from;
        RETURN NEXT;
    END LOOP;
END;
$$;

GRANT EXECUTE ON FUNCTION holdings_market_value_as_of(UUID, TIMESTAMPTZ, UUID, UUID) TO coffer_app;

-- ---------------------------------------------------------------------------
-- 2. Account cash balance as of an instant (date-bounded mig-133 view).
-- ---------------------------------------------------------------------------
CREATE FUNCTION account_balance_as_of(
    p_ledger_id  UUID,
    p_as_of      TIMESTAMPTZ,
    p_account_id UUID DEFAULT NULL
)
RETURNS TABLE(account_id UUID, balance NUMERIC)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    SELECT a.id,
           COALESCE(latest.balance_after, a.opening_balance)
    FROM accounts a
    LEFT JOIN LATERAL (
        SELECT thab.balance_after
        FROM txn_header_account_balances thab
        JOIN txn_headers h ON h.id = thab.header_id
        WHERE thab.account_id = a.id
          AND h.posted_at <= p_as_of
        ORDER BY h.posted_at DESC, h.seq DESC
        LIMIT 1
    ) latest ON TRUE
    WHERE a.ledger_id = p_ledger_id
      AND (p_account_id IS NULL OR a.id = p_account_id);
$$;

GRANT EXECUTE ON FUNCTION account_balance_as_of(UUID, TIMESTAMPTZ, UUID) TO coffer_app;
