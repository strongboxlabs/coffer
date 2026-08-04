-- =============================================================================
-- 060 — security_splits as metadata; drop 'split' action
-- =============================================================================
--
-- Background
-- ----------
-- Moneydance models stock splits as a separate `csplit` object on the
-- security itself, not as a transaction:
--
--   {
--     "obj_type": "csplit",
--     "id":       "<uuid>",
--     "curr":     "<security md-id>",
--     "dt":       20260519,        -- yyyymmdd
--     "ratio":    "2.0",           -- post-split qty multiplier
--     "oldshrs":  "2",             -- audit
--     "newshrs":  "1",             -- audit
--     "ts":       1779249083314    -- event millis (sub-day ordering)
--   }
--
-- MD's UI exposes splits in the Security Detail → History tab, NOT in
-- the transaction register. The data model follows: splits change the
-- security's outstanding-share count for every account that holds it,
-- so they're security metadata, not per-account events.
--
-- Coffer's pre-060 model treated splits as a `txn_headers.action='split'`
-- event with two same-account legs (`quantity = +new_qty`, holdings
-- side adjusted). That worked for the demo seed but doesn't round-trip
-- MD's csplit, and the action enum doesn't compose cleanly with the
-- ADR-0019 symmetric-postings shape (a split has no cash counterparty,
-- which makes the posting concept moot).
--
-- This migration:
--   1. Creates `security_splits` (one row per csplit; per-ledger,
--      RLS-policed, idempotent re-import keyed on external_id).
--   2. Migrates the demo seed's single IDXA 2-for-1 split (the only
--      action='split' row in the dev DB) to a security_splits row
--      and drops the header + legs.
--   3. Drops 'split' from the txn_headers.action CHECK.
--   4. Rewrites recompute_holdings_cost_basis to interleave splits
--      with txn_legs in chronological order, applying
--      running_qty *= ratio on split events. holdings.quantity is now
--      also driven by the function (was previously delta-summed in
--      the importer Pass 3 with NO awareness of splits).
-- =============================================================================


-- -----------------------------------------------------------------------------
-- Part 1: security_splits table
-- -----------------------------------------------------------------------------

CREATE TABLE security_splits (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    ledger_id    UUID NOT NULL,
    security_id  UUID NOT NULL,
    split_at     TIMESTAMPTZ NOT NULL,
    ratio        NUMERIC(25, 12) NOT NULL CHECK (ratio > 0),
    old_shares   NUMERIC(25, 12),
    new_shares   NUMERIC(25, 12),
    external_id  TEXT,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT security_splits_ledger_fk
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE RESTRICT,
    CONSTRAINT security_splits_security_fk
        FOREIGN KEY (security_id, ledger_id) REFERENCES securities(id, ledger_id) ON DELETE CASCADE
);

CREATE INDEX idx_security_splits_ledger_id ON security_splits(ledger_id);
CREATE INDEX idx_security_splits_security_split_at
    ON security_splits(security_id, split_at);

CREATE UNIQUE INDEX uq_security_splits_external_id_per_ledger
    ON security_splits(ledger_id, external_id)
    WHERE external_id IS NOT NULL;

COMMENT ON TABLE security_splits IS
    'Stock split / reverse-split events. One row per corporate action; '
    'split_at is the effective moment and ratio is the multiplier '
    'applied to share counts after that moment. Mirrors Moneydance''s '
    'csplit object; imported by SecuritySplitImportStep.';

COMMENT ON COLUMN security_splits.ratio IS
    'Multiplier applied to share counts at split_at. ratio=2.0 means a '
    '2-for-1 forward split (qty doubles); ratio=0.5 means a 1-for-2 '
    'reverse split (qty halves). Always > 0.';

COMMENT ON COLUMN security_splits.old_shares IS
    'Audit trail from MD csplit.oldshrs. Not used by the recompute '
    'function — ratio is the load-bearing field.';

COMMENT ON COLUMN security_splits.new_shares IS
    'Audit trail from MD csplit.newshrs. Not used by the recompute '
    'function — ratio is the load-bearing field.';

COMMENT ON COLUMN security_splits.external_id IS
    'Stable identity from the source system (MD csplit.id). NULL for '
    'user-created splits via the UI. Per-ledger unique when present.';

-- RLS: piggyback on securities — a row is visible iff its security
-- is visible to the current app user. Matches the security_prices
-- pattern (migration 014).
ALTER TABLE security_splits ENABLE ROW LEVEL SECURITY;

CREATE POLICY security_splits_per_user ON security_splits
    TO coffer_app
    USING (security_id IN (SELECT id FROM securities))
    WITH CHECK (security_id IN (SELECT id FROM securities));

GRANT SELECT, INSERT, UPDATE, DELETE ON security_splits TO coffer_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON security_splits TO coffer_service;


-- -----------------------------------------------------------------------------
-- Part 2: migrate existing action='split' headers → security_splits rows.
--
-- In production the dev DB has ONE such header (demo seed scenario 11:
-- IDXA 2-for-1 at 2026-05-10 09:00 UTC). Real MD imports never produced
-- an action='split' header because MD's csplit objects live outside the
-- txn stream. After this migration the demo seed (scripts/seed-demo-investments.sql)
-- is updated separately to write a security_splits row instead.
--
-- Migration logic:
--   - For each action='split' header, find the leg that names a security
--     and has quantity IS NOT NULL (the holdings-side row).
--   - Derive ratio: pre-split qty came from prior buy/divr legs; post-split
--     qty = pre-split + this leg's quantity. ratio = post / pre.
--   - Write a security_splits row at the header's posted_at.
--   - Delete the header (CASCADE deletes the legs).
-- -----------------------------------------------------------------------------

DO $$
DECLARE
    v_header RECORD;
    v_leg RECORD;
    v_pre_split_qty NUMERIC;
    v_post_split_qty NUMERIC;
    v_ratio NUMERIC;
    v_migrated INTEGER := 0;
BEGIN
    FOR v_header IN
        SELECT id, ledger_id, posted_at
        FROM txn_headers
        WHERE action = 'split'
    LOOP
        -- Holdings-side leg: the one with security_id AND quantity not null.
        SELECT account_id, security_id, quantity
        INTO v_leg
        FROM txn_legs
        WHERE header_id = v_header.id
          AND security_id IS NOT NULL
          AND quantity IS NOT NULL
        ORDER BY posting_index, id
        LIMIT 1;

        IF v_leg.security_id IS NULL THEN
            RAISE EXCEPTION
                'Migration 060: action=''split'' header % has no security-bearing leg with quantity.',
                v_header.id;
        END IF;

        -- Pre-split qty: sum of every quantity-bearing leg on this
        -- (account, security) at posted_at < header.posted_at. The leg
        -- value itself is the DELTA (e.g. +100 on a 2-for-1 doubling from
        -- 100 to 200), so post = pre + delta.
        SELECT COALESCE(SUM(l.quantity), 0)
        INTO v_pre_split_qty
        FROM txn_legs l
        JOIN txn_headers h ON h.id = l.header_id
        WHERE l.account_id  = v_leg.account_id
          AND l.security_id = v_leg.security_id
          AND l.quantity IS NOT NULL
          AND h.posted_at < v_header.posted_at;

        IF v_pre_split_qty <= 0 THEN
            RAISE EXCEPTION
                'Migration 060: cannot derive split ratio for header % — pre-split qty = %.',
                v_header.id, v_pre_split_qty;
        END IF;

        v_post_split_qty := v_pre_split_qty + v_leg.quantity;
        v_ratio := v_post_split_qty / v_pre_split_qty;

        INSERT INTO security_splits
            (id, ledger_id, security_id, split_at, ratio, old_shares, new_shares, external_id)
        VALUES
            (gen_random_uuid(), v_header.ledger_id, v_leg.security_id,
             v_header.posted_at, v_ratio, v_pre_split_qty, v_post_split_qty, NULL);

        -- Drop the header + legs. txn_legs has ON DELETE CASCADE via the
        -- header_id FK (migration 022).
        DELETE FROM txn_headers WHERE id = v_header.id;

        v_migrated := v_migrated + 1;
    END LOOP;

    RAISE NOTICE 'Migration 060: migrated % action=split header(s) to security_splits.', v_migrated;
END;
$$;


-- -----------------------------------------------------------------------------
-- Part 3: drop 'split' from the action CHECK.
-- -----------------------------------------------------------------------------

ALTER TABLE txn_headers DROP CONSTRAINT txn_headers_action_check;
ALTER TABLE txn_headers
    ADD CONSTRAINT txn_headers_action_check
    CHECK (action IS NULL OR action IN (
        'buy', 'sell',
        'dividend_cash', 'dividend_reinvest',
        'interest',
        'transfer',
        'misc_income', 'misc_expense'
    ));


-- -----------------------------------------------------------------------------
-- Part 4: rewrite recompute_holdings_cost_basis to interleave splits.
--
-- Same external signature as migration 054. Two changes:
--   (a) Build a unified event stream per holding: txn_legs + security_splits,
--       sorted by posted_at/split_at. Splits multiply running_qty by ratio;
--       basis is unchanged (an N-for-M split changes the per-share basis
--       but not the total).
--   (b) UPDATE holdings.quantity in addition to cost_basis. Previously the
--       importer Pass 3 (BulkUpsertHoldingsAsync aggregation) was the sole
--       writer of quantity, and it summed leg deltas with no split awareness.
--       Now the recompute walks the same event stream and emits the
--       authoritative qty, applying splits along the way.
-- -----------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(p_ledger_id UUID DEFAULT NULL)
RETURNS INTEGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_holding RECORD;
    v_event RECORD;
    v_lot RECORD;
    v_brokerage_include_fees BOOLEAN;
    v_running_qty NUMERIC;
    v_running_basis NUMERIC;
    v_avg_cost NUMERIC;
    v_fee NUMERIC;
    v_remaining_sell NUMERIC;
    v_updated INTEGER := 0;
BEGIN
    FOR v_holding IN
        SELECT id, account_id, security_id, ledger_id
        FROM holdings
        WHERE p_ledger_id IS NULL OR ledger_id = p_ledger_id
    LOOP
        -- Look up the brokerage's commission policy (migration 056).
        -- holdings.account_id is the Holdings sibling; the brokerage is
        -- the account whose holdings_account_id = that sibling.
        SELECT COALESCE(b.is_trade_commission, FALSE)
        INTO v_brokerage_include_fees
        FROM accounts b
        WHERE b.holdings_account_id = v_holding.account_id;
        v_brokerage_include_fees := COALESCE(v_brokerage_include_fees, FALSE);

        -- Idempotency reset: restore every lot for this holding to its
        -- acquired state with a freshly-computed unit_cost. Splits
        -- adjust lot quantity in-place during the walk; without this
        -- reset, a re-run would compound the multiplication.
        UPDATE lots l
        SET quantity  = tl.quantity,
            is_closed = FALSE,
            unit_cost = CASE
                WHEN v_brokerage_include_fees THEN
                    (tl.amount + COALESCE((
                        SELECT SUM(fl.amount)
                        FROM txn_legs fl
                        WHERE fl.header_id    = tl.header_id
                          AND fl.posting_role = 'fee'
                          AND fl.amount > 0
                    ), 0)) / tl.quantity
                ELSE
                    tl.amount / tl.quantity
            END
        FROM txn_legs tl
        WHERE l.holding_id = v_holding.id
          AND l.leg_id     = tl.id;

        v_running_qty   := 0;
        v_running_basis := 0;

        -- Unified event stream: leg events (kind='leg') interleaved with
        -- split events (kind='split'). Ordered by event_at, then kind
        -- ('leg' before 'split' on same instant) so a split posted at the
        -- exact moment of a leg applies AFTER the leg lands.
        FOR v_event IN
            SELECT
                'leg'::TEXT AS kind,
                hd.posted_at AS event_at,
                l.id AS leg_id,
                l.header_id,
                l.amount,
                l.quantity,
                NULL::NUMERIC AS ratio
            FROM txn_legs l
            JOIN txn_headers hd ON hd.id = l.header_id
            WHERE l.security_id = v_holding.security_id
              AND l.account_id  = v_holding.account_id
              AND l.quantity IS NOT NULL

            UNION ALL

            SELECT
                'split'::TEXT AS kind,
                ss.split_at AS event_at,
                NULL::UUID AS leg_id,
                NULL::UUID AS header_id,
                NULL::NUMERIC AS amount,
                NULL::NUMERIC AS quantity,
                ss.ratio
            FROM security_splits ss
            WHERE ss.security_id = v_holding.security_id
              AND ss.ledger_id   = v_holding.ledger_id

            ORDER BY event_at, kind, leg_id
        LOOP
            IF v_event.kind = 'split' THEN
                -- Apply the multiplier to the running qty AND every open
                -- lot for this holding. Basis is preserved (per-share
                -- basis updates implicitly via the qty change). Closed
                -- lots stay closed at quantity=0.
                v_running_qty := v_running_qty * v_event.ratio;

                UPDATE lots
                SET quantity = quantity * v_event.ratio
                WHERE holding_id = v_holding.id
                  AND is_closed  = FALSE;

            ELSIF v_event.quantity > 0 THEN
                -- Acquisition. Fee inclusion gated on the brokerage's
                -- is_trade_commission flag (migration 056); posting_role
                -- is the source of truth for fee identification.
                IF v_brokerage_include_fees THEN
                    v_fee := COALESCE((
                        SELECT SUM(fl.amount)
                        FROM txn_legs fl
                        WHERE fl.header_id    = v_event.header_id
                          AND fl.posting_role = 'fee'
                          AND fl.amount > 0
                    ), 0);
                ELSE
                    v_fee := 0;
                END IF;
                v_running_qty   := v_running_qty + v_event.quantity;
                v_running_basis := v_running_basis + v_event.amount + v_fee;

            ELSIF v_event.quantity < 0 AND v_running_qty > 0 THEN
                -- Disposition: avg-cost basis reduction.
                v_avg_cost      := v_running_basis / v_running_qty;
                v_running_basis := v_running_basis - (v_avg_cost * ABS(v_event.quantity));
                v_running_qty   := v_running_qty + v_event.quantity;
                IF v_running_qty <= 0 THEN
                    v_running_qty   := 0;
                    v_running_basis := 0;
                END IF;

                -- FIFO lot closure.
                v_remaining_sell := ABS(v_event.quantity);
                FOR v_lot IN
                    SELECT id, quantity
                    FROM lots
                    WHERE holding_id = v_holding.id
                      AND is_closed  = FALSE
                      AND quantity   > 0
                    ORDER BY acquired_at, id
                LOOP
                    EXIT WHEN v_remaining_sell <= 0;

                    IF v_lot.quantity <= v_remaining_sell THEN
                        UPDATE lots
                        SET quantity  = 0,
                            is_closed = TRUE
                        WHERE id = v_lot.id;
                        v_remaining_sell := v_remaining_sell - v_lot.quantity;
                    ELSE
                        UPDATE lots
                        SET quantity = quantity - v_remaining_sell
                        WHERE id = v_lot.id;
                        v_remaining_sell := 0;
                    END IF;
                END LOOP;
            END IF;
        END LOOP;

        UPDATE holdings
        SET cost_basis = v_running_basis,
            quantity   = v_running_qty
        WHERE id = v_holding.id;

        v_updated := v_updated + 1;
    END LOOP;

    RETURN v_updated;
END;
$$;

COMMENT ON FUNCTION recompute_holdings_cost_basis(UUID) IS
    'Walks the unified (txn_legs ∪ security_splits) event stream per holding '
    'in chronological order (migration 060). Reads the brokerage''s '
    'is_trade_commission flag (056) to decide whether posting_role=''fee'' '
    'amounts flow into basis. Increments basis + qty on Buy / DivReinvest, '
    'reduces both on Sell via avg-cost + FIFO lot closure, and multiplies '
    'running_qty + every open lot by ratio on stock splits. Authoritative '
    'writer of holdings.quantity and holdings.cost_basis from 060 onward.';


-- -----------------------------------------------------------------------------
-- Part 5: one-shot scrub.
-- -----------------------------------------------------------------------------

SELECT recompute_holdings_cost_basis(NULL);
