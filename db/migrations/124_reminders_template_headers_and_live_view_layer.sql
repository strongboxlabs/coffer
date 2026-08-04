-- =============================================================================
-- 124 — reminders foundation: template-in-txn_headers + recurring_transactions
--        reshape + live/template view layer (ADR-0047 / ADR-0048)
-- =============================================================================
--
-- ADR-0047: a reminder's transaction is a real txn_header + txn_legs flagged
-- is_recurring_template (a template, never a live cash event). ADR-0048: the
-- live/template partition is enforced ONCE on a light header-level view
-- (live_txn_headers); legs follow their header via the join, so no
-- live_txn_legs and no leg-flag denormalization. Reads go through views; the
-- recompute reads live_txn_headers; writes go to tables via EF.
--
-- This migration reshapes the schema and repoints the read surfaces. It does
-- NOT convert the existing recurring_transactions rows (ADR-0048 D6, option
-- B): every such row is origin='moneydance_import' with an external_id and is
-- idempotently re-importable, so the rewritten importer re-materializes each
-- as a template header+legs on the next MD import — through the single
-- validated construction path, rather than a second, divergent SQL builder.
-- Until re-import, a reshaped row has null rrule/template_header_id and is
-- dormant (no template header → never appears in the reminders surface).
--
-- No one-shot recompute: is_recurring_template defaults FALSE, so there are
-- no templates yet; live_txn_headers == txn_headers for all current rows and
-- every balance/holding is unchanged.
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 1. txn_headers — template discriminator + fired-occurrence back-reference
-- ---------------------------------------------------------------------------
ALTER TABLE txn_headers
    ADD COLUMN is_recurring_template    BOOLEAN NOT NULL DEFAULT FALSE,
    -- A FIRED occurrence (committed header) points back to its series + slot
    -- (ADR-0047 D5). NULL on ordinary rows AND on the template itself.
    ADD COLUMN recurring_transaction_id UUID,
    ADD COLUMN occurrence_date          DATE;

COMMENT ON COLUMN txn_headers.is_recurring_template IS
    'ADR-0047: TRUE marks this header (and its legs) a recurring TEMPLATE — '
    'never a live cash event. Excluded from every live read surface via the '
    'live_txn_headers view (ADR-0048); never enters the balance/holdings walk.';

-- Partition support: a small partial index over the template set (tiny).
CREATE INDEX idx_txn_headers_recurring_template
    ON txn_headers (id) WHERE is_recurring_template;
-- Fired occurrences -> their series.
CREATE INDEX idx_txn_headers_recurring_transaction_id
    ON txn_headers (recurring_transaction_id, ledger_id)
    WHERE recurring_transaction_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- 2. recurring_transactions — slim to recurrence metadata + a template pointer
-- ---------------------------------------------------------------------------
ALTER TABLE recurring_transactions
    ADD COLUMN rrule                   TEXT,
    ADD COLUMN source_payload          JSONB,
    ADD COLUMN auto_commit_days_before INTEGER,
    ADD COLUMN template_header_id      UUID,
    -- Needed so txn_headers.recurring_transaction_id can be a composite,
    -- ledger-scoped FK (the mig-049/072 coherence pattern).
    ADD CONSTRAINT uq_recurring_transactions_id_ledger UNIQUE (id, ledger_id);

COMMENT ON COLUMN recurring_transactions.rrule IS
    'ADR-0047: RFC 5545 recurrence rule (replaces the discrete frequency '
    'columns). Expanded by the C# RecurrenceExpander.';
COMMENT ON COLUMN recurring_transactions.source_payload IS
    'ADR-0047: raw Moneydance reminder object, lossless (provider_raw_payload '
    'pattern). Preserves splits / acdays / anything the structured model omits.';
COMMENT ON COLUMN recurring_transactions.auto_commit_days_before IS
    'ADR-0047: MD acdays. NULL = manual approve; N>=0 = auto-commit N days '
    'before the due date (the firing worker is a later slice).';
COMMENT ON COLUMN recurring_transactions.template_header_id IS
    'ADR-0047: the template txn_header carrying this series'' transaction shape '
    '(its legs are the splits). FK is DEFERRABLE for the mutual reference with '
    'txn_headers.recurring_transaction_id during snapshot restore (ADR-0048).';

-- template_header_id -> txn_headers. DEFERRABLE INITIALLY DEFERRED: this and
-- txn_headers.recurring_transaction_id reference each other, so a bulk insert
-- (snapshot restore) can't satisfy both row-at-a-time; the deferred check
-- resolves at commit. Normal writes never hit the cycle (a template header is
-- inserted with recurring_transaction_id NULL, then the series row points at
-- it). ON DELETE RESTRICT: a template header can't be deleted out from under
-- its series; the series-delete flow removes the template explicitly.
ALTER TABLE recurring_transactions
    ADD CONSTRAINT recurring_transactions_template_header_fkey
        FOREIGN KEY (template_header_id, ledger_id)
        REFERENCES txn_headers (id, ledger_id)
        ON DELETE RESTRICT
        DEFERRABLE INITIALLY DEFERRED;

-- The fired-occurrence link. ON DELETE SET NULL: hard-deleting a series leaves
-- its already-committed occurrences intact (ADR-0047 D6 — committed cash is
-- immutable), merely unlinked.
ALTER TABLE txn_headers
    ADD CONSTRAINT txn_headers_recurring_transaction_fkey
        FOREIGN KEY (recurring_transaction_id, ledger_id)
        REFERENCES recurring_transactions (id, ledger_id)
        ON DELETE SET NULL;

-- Drop the denormalized transaction-shape columns: the shape now lives on the
-- template header + legs (+ rrule). Existing rows are re-materialized by the
-- importer (option B above) — not converted here. DROP COLUMN cascades the
-- old per-column FKs/indexes/CHECKs.
ALTER TABLE recurring_transactions
    DROP COLUMN source_account_id,
    DROP COLUMN target_account_id,
    DROP COLUMN description,
    DROP COLUMN memo,
    DROP COLUMN amount,
    DROP COLUMN frequency,
    DROP COLUMN monthly_day,
    DROP COLUMN weekly_dow,
    DROP COLUMN interval_units;

-- ---------------------------------------------------------------------------
-- 3. Layer-1 light partition views (ADR-0048 D1). Plain single-table filter
--    views so Postgres inlines them (view-on-view plans like the base query).
--    security_invoker so RLS on txn_headers applies per caller, like
--    resolved_transactions.
-- ---------------------------------------------------------------------------
CREATE VIEW live_txn_headers     AS SELECT * FROM txn_headers WHERE NOT is_recurring_template;
CREATE VIEW template_txn_headers AS SELECT * FROM txn_headers WHERE     is_recurring_template;

ALTER VIEW live_txn_headers     SET (security_invoker = true);
ALTER VIEW template_txn_headers SET (security_invoker = true);

GRANT SELECT ON live_txn_headers, template_txn_headers TO coffer_app;
GRANT ALL    ON live_txn_headers, template_txn_headers TO coffer_service;

-- ---------------------------------------------------------------------------
-- 4. resolved_transactions rebuilt on live_txn_headers (Layer 2). Verbatim
--    from mig 122 with the ONLY change being the header source:
--    `JOIN txn_headers h` -> `JOIN live_txn_headers h`. Template legs whose
--    header is not in live_txn_headers drop out of the inner join (ADR-0048
--    D1). The `other` counterparty self-join stays raw txn_legs — it is the
--    sibling posting of the SAME (already-live) header.
-- ---------------------------------------------------------------------------
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
    thab.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    h.action AS investment_action,
    h.external_id,
    l.created_at,
    COALESCE(o.check_number, h.check_number) AS check_number,
    other.id AS counterparty_id,
        CASE
            WHEN l.header_total_postings > 1 THEN h.id
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
    h.needs_review,
    COALESCE(l.security_id, other.security_id) AS security_id,
    s.ticker AS security_ticker,
    s.name AS security_name,
    COALESCE(l.quantity, other.quantity) AS quantity,
    COALESCE(l.unit_price, other.unit_price) AS unit_price,
    l.posting_role,
    h.ingest_action_hint,
    psm.security_id AS ingest_security_id,
    h.ingest_shares,
    h.ingest_unit_price,
    h.ingest_fee,
    h.ingest_security_ticker_hint,
    h.provider_raw_payload,
    h.seq AS header_seq,
    thab.net_amount AS header_account_net_amount,
    h.provider_key,
    h.is_merge_winner,
    h.import_source,
    l.account_postings_on_header,
    l.header_total_postings,
    COALESCE(
        h.action,
        CASE
            WHEN this_account.account_type <> 'category'
                AND ca.account_type IS NOT NULL
                AND ca.account_type <> 'category'
                THEN 'Xfr'
            ELSE NULL
        END
    ) AS derived_action,
    this_account.account_type AS account_type
   FROM txn_legs l
     JOIN live_txn_headers h ON h.id = l.header_id
     JOIN accounts this_account ON this_account.id = l.account_id
     LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
     LEFT JOIN txn_legs other ON other.header_id = l.header_id AND other.posting_index = l.posting_index AND other.id <> l.id
     LEFT JOIN accounts ca ON ca.id = other.account_id
     LEFT JOIN securities s ON s.id = COALESCE(l.security_id, other.security_id)
     LEFT JOIN txn_header_account_balances thab
            ON thab.header_id = h.id AND thab.account_id = l.account_id
     LEFT JOIN provider_security_mappings psm
            ON psm.ledger_id = h.ledger_id
           AND psm.provider_key = h.provider_key
           AND psm.provider_security_id = h.ingest_security_ticker_hint;

ALTER VIEW resolved_transactions SET (security_invoker = true);
GRANT SELECT ON resolved_transactions TO coffer_app;
GRANT ALL    ON resolved_transactions TO coffer_service;

-- ---------------------------------------------------------------------------
-- 5. fn_recompute_balances_for_account reads live_txn_headers (ADR-0048 D2).
--    Verbatim from mig 103 with every `txn_headers` read swapped to
--    `live_txn_headers`; the is_hidden / is_merged_into predicates stay (they
--    are per-row axes, not the template partition). thab is written, not read
--    through a view.
-- ---------------------------------------------------------------------------
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

    SELECT thab.balance_after
      INTO v_starting
      FROM txn_header_account_balances thab
      JOIN live_txn_headers h ON h.id = thab.header_id
      LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     WHERE thab.account_id = p_account_id
       AND COALESCE(o.posted_at, h.posted_at) < p_from_posted_at
     ORDER BY COALESCE(o.posted_at, h.posted_at) DESC, h.seq DESC
     LIMIT 1;

    IF v_starting IS NULL THEN
        SELECT a.opening_balance INTO v_starting FROM accounts a WHERE a.id = p_account_id;
    END IF;
    v_starting := COALESCE(v_starting, 0);

    DELETE FROM txn_header_account_balances thab
     USING live_txn_headers h
      LEFT JOIN txn_header_overrides o ON o.header_id = h.id
     WHERE thab.header_id = h.id
       AND thab.account_id = p_account_id
       AND COALESCE(o.posted_at, h.posted_at) >= p_from_posted_at;

    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after, net_amount)
    WITH header_net AS (
        SELECT h.id AS header_id,
               COALESCE(o.posted_at, h.posted_at) AS posted_at,
               h.seq,
               SUM(COALESCE(lo.amount, l.amount)) AS net_amount
          FROM live_txn_headers h
          JOIN txn_legs l ON l.header_id = h.id
          LEFT JOIN txn_leg_overrides lo ON lo.leg_id = l.id
          LEFT JOIN txn_header_overrides o ON o.header_id = h.id
         WHERE l.account_id = p_account_id
           AND h.is_merged_into IS NULL
           AND COALESCE(o.is_hidden, h.is_hidden, FALSE) = FALSE
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

-- ---------------------------------------------------------------------------
-- 6. recompute_holdings_cost_basis reads live_txn_headers (ADR-0048 D2).
--    Verbatim from mig 118 with the two `txn_headers` joins swapped to
--    `live_txn_headers`; the is_hidden predicates stay.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION recompute_holdings_cost_basis(
    p_ledger_id   UUID DEFAULT NULL,
    p_account_id  UUID DEFAULT NULL,
    p_security_id UUID DEFAULT NULL
)
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
    v_resolved_ledger_id UUID;
BEGIN
    IF p_account_id IS NOT NULL AND p_security_id IS NOT NULL THEN
        SELECT ledger_id INTO v_resolved_ledger_id
        FROM accounts WHERE id = p_account_id;

        IF v_resolved_ledger_id IS NOT NULL
           AND (p_ledger_id IS NULL OR p_ledger_id = v_resolved_ledger_id)
           AND NOT EXISTS (
               SELECT 1 FROM holdings
               WHERE account_id  = p_account_id
                 AND security_id = p_security_id
           )
        THEN
            INSERT INTO holdings (id, account_id, security_id, ledger_id, quantity, cost_basis, as_of)
            VALUES (gen_random_uuid(), p_account_id, p_security_id, v_resolved_ledger_id, 0, 0, NOW());
        END IF;
    END IF;

    FOR v_holding IN
        SELECT id, account_id, security_id, ledger_id
        FROM holdings
        WHERE (p_ledger_id   IS NULL OR ledger_id   = p_ledger_id)
          AND (p_account_id  IS NULL OR account_id  = p_account_id)
          AND (p_security_id IS NULL OR security_id = p_security_id)
    LOOP
        SELECT COALESCE(b.is_trade_commission, FALSE)
        INTO v_brokerage_include_fees
        FROM accounts b
        WHERE b.holdings_account_id = v_holding.account_id;
        v_brokerage_include_fees := COALESCE(v_brokerage_include_fees, FALSE);

        UPDATE lots l
        SET quantity  = tl.quantity,
            is_closed = FALSE,
            unit_cost = CASE
                WHEN tl.quantity = 0 THEN 0
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
        JOIN live_txn_headers th ON th.id = tl.header_id
        WHERE l.holding_id = v_holding.id
          AND l.leg_id     = tl.id
          AND th.is_hidden = FALSE;

        v_running_qty   := 0;
        v_running_basis := 0;

        FOR v_event IN
            SELECT
                'leg'::TEXT AS kind,
                hd.posted_at AS event_at,
                l.id AS leg_id,
                l.header_id,
                l.amount,
                l.quantity,
                NULL::NUMERIC AS ratio,
                CASE
                    WHEN l.quantity > 0 THEN 1
                    ELSE 2
                END AS sort_class
            FROM txn_legs l
            JOIN live_txn_headers hd ON hd.id = l.header_id
            WHERE l.security_id = v_holding.security_id
              AND l.account_id  = v_holding.account_id
              AND l.quantity IS NOT NULL
              AND hd.is_hidden = FALSE

            UNION ALL

            SELECT
                'split'::TEXT AS kind,
                ss.split_at AS event_at,
                NULL::UUID AS leg_id,
                NULL::UUID AS header_id,
                NULL::NUMERIC AS amount,
                NULL::NUMERIC AS quantity,
                ss.ratio,
                0 AS sort_class
            FROM security_splits ss
            WHERE ss.security_id = v_holding.security_id
              AND ss.ledger_id   = v_holding.ledger_id

            ORDER BY event_at, sort_class, leg_id
        LOOP
            IF v_event.kind = 'split' THEN
                v_running_qty := v_running_qty * v_event.ratio;

                UPDATE lots
                SET quantity = quantity * v_event.ratio
                WHERE holding_id = v_holding.id
                  AND is_closed  = FALSE;

            ELSIF v_event.quantity > 0 THEN
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

            ELSIF v_event.quantity < 0 THEN
                IF v_running_qty > 0 THEN
                    v_avg_cost := v_running_basis / v_running_qty;
                    v_running_basis := v_running_basis
                        - (v_avg_cost * LEAST(v_running_qty, ABS(v_event.quantity)));
                END IF;
                v_running_qty := v_running_qty + v_event.quantity;

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

-- ---------------------------------------------------------------------------
-- Snapshot functions (mig 112): NO change needed. fn_ledger_snapshot_payload
-- uses to_jsonb(t) and the restore uses jsonb_populate_recordset, so the
-- reshaped recurring_transactions + the new txn_headers columns round-trip
-- automatically. The new mutual FK (recurring_transactions.template_header_id
-- <-> txn_headers.recurring_transaction_id) is resolved by the DEFERRABLE
-- INITIALLY DEFERRED constraint above: the restore inserts recurring_transactions
-- (deferred check) then txn_headers, and both FKs validate at commit.
-- ---------------------------------------------------------------------------
