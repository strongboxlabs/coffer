-- =============================================================================
-- 257 — re-issue text Postgres stores, after a privacy rewrite of its source
-- =============================================================================
--
-- WHY
-- ---
-- The repo's prose carries no account names and no amounts, real or invented
-- (a preflight gate enforces the shape). Making it so meant
-- rewriting comments in shipped migrations, which engineering-standards.md §3.1
-- allows for comments only. A comment in a migration's header is gone once the
-- script has run, so editing it changes nothing an install holds. Two kinds of
-- text are different, because Postgres keeps them:
--
--   * COMMENT ON text, in pg_description;
--   * a function's body, comments included, in pg_proc.prosrc.
--
-- Editing those in their original migrations reaches fresh installs only. This
-- migration re-issues each one that is still current, so an upgraded install ends
-- up holding exactly what a fresh one does.
--
-- WHAT
-- ----
-- Every statement below is copied verbatim from the migration that last defined
-- the object (named above each one). No code changes: each CREATE OR REPLACE has
-- the signature, attributes and body of the current definition, with only its
-- comments rewritten, so grants and ownership carry over untouched. An object a
-- later migration replaced or dropped is not here, since its old text is stored
-- nowhere: that is why 059's trigger function, dropped by 061, is absent.
-- =============================================================================

BEGIN;

-- From 137_scheduled_jobs_timezone.sql.
COMMENT ON COLUMN scheduled_jobs.timezone IS
    'IANA timezone (an Area/Location name) the hour_local/minute_local are '
    'interpreted in — the user''s browser tz at save. NULL = fall back to the '
    'server local tz.';


-- From 209_realized_gains_round_once.sql.
COMMENT ON FUNCTION recompute_holdings_cost_basis(UUID, UUID, UUID) IS
    'ADR-0064 FIFO basis writer. Since mig 202 a thin persist over holdings_fifo_walk, '
    'so the algorithm is shared with the as-of read path and the two cannot drift. '
    'Bounds what it stores at each column''s OWN scale (money 2dp per mig 182, shares '
    '12dp) — mig 205 rounded money to 4dp believing these columns were unbounded, but '
    'mig 182 had already narrowed them to NUMERIC(19,2), so that rounded twice and '
    'shifted some disposals by a cent (mig 209).';


-- From 222_feed_csv_mappings.sql.
COMMENT ON TABLE feed_csv_mappings IS
    'Reusable, hand-editable description of one institution''s delimited export '
    '(ADR-0031 Phase 5). The definition is a YAML document rather than typed columns '
    'because real exports vary in many small ways and a schema that needs a '
    'migration per institution is the wrong shape. Validation lives in '
    'CsvMappingValidator, which is STRICTER than the CHECKs it replaced — unknown keys '
    'are rejected, because a mistyped key that is silently ignored would leave a field '
    'unmapped and import the wrong column as money.';


-- From 226_register_scopes_to_many_accounts.sql.
CREATE OR REPLACE FUNCTION public.register_entry_keys(p_account_ids uuid[], p_ledger_id uuid, p_cursor_entry_key uuid, p_cursor_seq bigint, p_direction text, p_limit integer, p_hidden boolean DEFAULT false, p_search text DEFAULT NULL::text, p_date_from date DEFAULT NULL::date, p_date_to date DEFAULT NULL::date, p_amount_min numeric DEFAULT NULL::numeric, p_amount_max numeric DEFAULT NULL::numeric, p_security_id uuid DEFAULT NULL::uuid, p_tag text DEFAULT NULL::text, p_category_id uuid DEFAULT NULL::uuid, p_status text DEFAULT NULL::text, p_today date DEFAULT NULL::date, p_sort_column text DEFAULT 'date'::text, p_sort_dir text DEFAULT 'desc'::text)
 RETURNS TABLE(posted_at timestamp with time zone, seq bigint, entry_key uuid)
 LANGUAGE plpgsql
 STABLE PARALLEL SAFE
AS $function$
DECLARE
    -- ADR-0036 asymmetric entry key: leg id for a target-split entry (the
    -- account touches fewer legs than the header has), else the header id.
    v_entry_key CONSTANT TEXT :=
        'CASE WHEN rt.account_postings_on_header < rt.header_total_postings '
        'THEN rt.id ELSE rt.header_id END';

    v_sort_expr TEXT;   -- the entry's sort value: a coalesced MAX() aggregate
    v_dir       TEXT;   -- 'ASC' | 'DESC' — the display direction
    v_fetch_dir TEXT;   -- 'ASC' | 'DESC' — inner fetch dir (reversed for 'after')
    v_op        TEXT;   -- '<' | '>'      — keyset comparison operator
    v_cursor_val TEXT;  -- SQL deriving the cursor entry's sort value from its key
BEGIN
    -- Whitelist: sort column → aggregate expression. Unknown → 'date'. Values
    -- are coalesced non-null so the keyset stays a plain row comparison.
    v_sort_expr := CASE p_sort_column
        WHEN 'amount'   THEN 'MAX(COALESCE(rt.header_account_net_amount, rt.amount))'
        WHEN 'payee'    THEN 'COALESCE(MAX(rt.payee), '''')'
        WHEN 'category' THEN 'COALESCE(MAX(rt.counterparty_account_name), '''')'
        WHEN 'security' THEN 'COALESCE(MAX(rt.security_ticker), '''')'
        WHEN 'shares'   THEN 'COALESCE(MAX(rt.quantity), 0)'
        WHEN 'price'    THEN 'COALESCE(MAX(rt.unit_price), 0)'
        WHEN 'action'   THEN 'COALESCE(MAX(rt.derived_action), '''')'
        ELSE                 'MAX(rt.posted_at)'   -- 'date' (default)
    END;

    v_dir := CASE WHEN lower(p_sort_dir) = 'asc' THEN 'ASC' ELSE 'DESC' END;

    -- p_direction='before' = the next page in display order (scroll down);
    -- 'after' = the previous page (scroll up), fetched in reverse then
    -- re-sorted to display order by the outer query. The keyset operator walks
    -- AWAY from the cursor in the requested direction:
    --   display DESC → 'before' uses '<' (older/smaller), 'after' uses '>'
    --   display ASC  → 'before' uses '>' (larger),        'after' uses '<'
    IF p_direction = 'after' THEN
        v_fetch_dir := CASE WHEN v_dir = 'ASC' THEN 'DESC' ELSE 'ASC' END;
        v_op        := CASE WHEN v_dir = 'ASC' THEN '<' ELSE '>' END;
    ELSE
        v_fetch_dir := v_dir;
        v_op        := CASE WHEN v_dir = 'ASC' THEN '>' ELSE '<' END;
    END IF;

    -- The cursor entry's sort value, derived from its key (param 16). The OR-match
    -- resolves to exactly the cursor entry's legs (header-id match for a normal
    -- entry, leg-id match for a target split), so the implicit aggregate yields
    -- a single scalar. Same visibility/scope predicate as the primitive; this
    -- is a point lookup by key, not a filter, so it reads the view directly.
    v_cursor_val := format(
        '(SELECT %s FROM resolved_transactions rt '
        'WHERE (rt.header_id = $16 OR rt.id = $16) '
        'AND rt.is_hidden = $3 AND rt.is_merged_into IS NULL '
        'AND ($1 IS NULL OR rt.account_id = ANY($1)) '
        'AND ($1 IS NOT NULL OR EXISTS ('
        'SELECT 1 FROM accounts a WHERE a.id = rt.account_id AND a.ledger_id = $2)))',
        v_sort_expr);

    RETURN QUERY EXECUTE format($q$
        SELECT posted_at, seq, entry_key FROM (
            SELECT
                MAX(rt.posted_at)  AS posted_at,
                MAX(rt.header_seq) AS seq,
                %1$s               AS entry_key,
                %2$s               AS sort_val
            -- Single source of truth for the filter (mig 167). Inlines into
            -- this keyset query — no plan change vs the pre-167 inline WHERE.
            FROM register_filtered_entries($1, $2, $3, $11, $4, $5, $6, $7, $8, $10, $9, $12, $13) rt
            GROUP BY %1$s
            -- Keyset: entries strictly past the cursor in the fetch direction.
            -- Non-null sort values ⇒ a plain 3-tuple row comparison; entry_key
            -- is the final tiebreaker so the order is total.
            HAVING $16 IS NULL
               OR (%2$s, MAX(rt.header_seq), %1$s) %3$s (%4$s, $15, $16)
            ORDER BY sort_val %5$s, seq %5$s, entry_key %5$s
            LIMIT $14
        ) sub
        ORDER BY sort_val %6$s, seq %6$s, entry_key %6$s
    $q$, v_entry_key, v_sort_expr, v_op, v_cursor_val, v_fetch_dir, v_dir)
    USING p_account_ids,       -- param 1
          p_ledger_id,         -- param 2
          p_hidden,            -- param 3
          p_date_from,         -- param 4
          p_date_to,           -- param 5
          p_amount_min,        -- param 6
          p_amount_max,        -- param 7
          p_security_id,       -- param 8
          p_category_id,       -- param 9
          p_tag,               -- param 10
          p_search,            -- param 11
          p_status,            -- param 12
          p_today,             -- param 13
          p_limit,             -- param 14
          p_cursor_seq,        -- param 15
          p_cursor_entry_key;  -- param 16
END;
$function$
;

-- From 227_retire_lots_of_merged_headers.sql.
CREATE OR REPLACE FUNCTION public.recompute_holdings_cost_basis(p_ledger_id uuid DEFAULT NULL::uuid, p_account_id uuid DEFAULT NULL::uuid, p_security_id uuid DEFAULT NULL::uuid)
 RETURNS integer
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_holding RECORD;
    v_walk    RECORD;
    v_updated INTEGER := 0;
    v_resolved_ledger_id UUID;
BEGIN
    IF p_account_id IS NOT NULL AND p_security_id IS NOT NULL THEN
        SELECT ledger_id INTO v_resolved_ledger_id FROM accounts WHERE id = p_account_id;
        IF v_resolved_ledger_id IS NOT NULL
           AND (p_ledger_id IS NULL OR p_ledger_id = v_resolved_ledger_id)
           AND NOT EXISTS (SELECT 1 FROM holdings
                           WHERE account_id = p_account_id AND security_id = p_security_id)
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
        SELECT * INTO v_walk
        FROM holdings_fifo_walk(v_holding.account_id, v_holding.security_id, NULL);

        DELETE FROM realized_gains
        WHERE account_id = v_holding.account_id AND security_id = v_holding.security_id;

        IF COALESCE(array_length(v_walk.o_gains, 1), 0) > 0 THEN
            INSERT INTO realized_gains (
                ledger_id, account_id, security_id, sell_leg_id,
                sold_at, quantity, proceeds, cost_basis_sold, realized_gain,
                proceeds_lt, cost_basis_sold_lt, realized_gain_lt)
            SELECT v_holding.ledger_id, v_holding.account_id, v_holding.security_id,
                   g.sell_leg_id, g.sold_at,
                   ROUND(g.quantity, 12),
                   ROUND(g.proceeds, 2),
                   ROUND(g.cost_basis_sold, 2),
                   ROUND(g.realized_gain, 2),
                   ROUND(g.proceeds_lt, 2),
                   ROUND(g.cost_basis_sold_lt, 2),
                   ROUND(g.realized_gain_lt, 2)
            FROM unnest(v_walk.o_gains) AS g;
        END IF;

        -- Lot rows are created by the write path; here they are brought to the state
        -- the walk derived. The lots columns carry their own precision, so the
        -- assignment bounds these.
        IF COALESCE(array_length(v_walk.o_lots, 1), 0) > 0 THEN
            UPDATE lots l
            SET quantity  = w.quantity,
                unit_cost = w.unit_cost,
                is_closed = w.is_closed
            FROM unnest(v_walk.o_lots) AS w
            WHERE l.holding_id = v_holding.id
              AND l.leg_id     = w.leg_id;
        END IF;

        -- ...and retire the lots the walk did NOT produce. Those belong to legs on
        -- a hidden or merged header: the walk excludes them (mig 163), so until now
        -- they were "left untouched", which meant left at whatever quantity they
        -- held BEFORE the header was hidden or merged.
        --
        -- Holdings were right and the lots were not. Merging a duplicate buy into
        -- its winner left holdings.quantity at the winner's shares and left the
        -- loser's lot open beside the winner's — open lots summing to more shares
        -- than the position holds. The lots table is derived (the FIFO walk
        -- reads txn_legs, not lots), so nothing recomputed itself back into
        -- agreement, and every reader of lots saw the phantom.
        UPDATE lots l
        SET quantity  = 0,
            is_closed = TRUE
        WHERE l.holding_id = v_holding.id
          AND NOT l.is_closed
          AND NOT EXISTS (
              SELECT 1 FROM unnest(v_walk.o_lots) AS w WHERE w.leg_id = l.leg_id
          );

        UPDATE holdings
        SET cost_basis = ROUND(v_walk.o_cost_basis, 4),
            quantity   = ROUND(v_walk.o_quantity, 12)
        WHERE id = v_holding.id;

        v_updated := v_updated + 1;
    END LOOP;

    RETURN v_updated;
END;
$function$;

-- From 247_snapshot_restore_completeness.sql.
CREATE OR REPLACE FUNCTION fn_snapshot_write_part(
    p_snapshot_id uuid,
    p_ledger_id   uuid,
    p_table       text,
    p_chunk_rows  integer
)
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    -- Ceiling for the unchunked path only. Two uuid columns per row, so even at
    -- this many rows the aggregate is tens of MB — far from the multi-GB peak
    -- that mig 193 set out to eliminate — but it fails rather than degrading
    -- quietly.
    c_unchunked_max CONSTANT bigint := 500000;

    v_has_id  boolean;
    v_count   bigint;
    v_written bigint  := 0;
    v_last    uuid    := '00000000-0000-0000-0000-000000000000';
    v_seq     integer := 0;
    v_rows    integer;
    v_chunk   jsonb;
    v_bytes   bigint  := 0;
BEGIN
    SELECT EXISTS (
        SELECT 1
          FROM pg_attribute a
         WHERE a.attrelid = p_table::regclass
           AND a.attname  = 'id'
           AND a.attnum   > 0
           AND NOT a.attisdropped)
      INTO v_has_id;

    EXECUTE format('SELECT count(*) FROM %I t WHERE t.ledger_id = $1', p_table)
      INTO v_count USING p_ledger_id;

    IF NOT v_has_id THEN
        IF v_count > c_unchunked_max THEN
            RAISE EXCEPTION
                'snapshot part %: % rows and no id column to key a chunked scan on. '
                'Add a composite keyset for this table before it grows further; '
                'capturing it in one aggregate would reintroduce the mig 193 OOM.',
                p_table, v_count;
        END IF;

        EXECUTE format(
            'SELECT COALESCE(jsonb_agg(to_jsonb(t)), ''[]''::jsonb)
               FROM %I t WHERE t.ledger_id = $1', p_table)
          INTO v_chunk USING p_ledger_id;

        -- seq=0 is written even when empty: fn_ledger_snapshot_restore (mig 188)
        -- distinguishes an absent payload key from an empty '[]'.
        INSERT INTO ledger_snapshot_parts (snapshot_id, part_name, seq, content)
        VALUES (p_snapshot_id, p_table, 0, v_chunk);

        v_written := jsonb_array_length(v_chunk);
        v_bytes   := octet_length(v_chunk::text);
    ELSE
        LOOP
            -- First chunk from the all-zero uuid inclusive, then strictly after
            -- the last id written; both forms stay a plain index condition.
            EXECUTE format(
                'WITH c AS (
                     SELECT t.id AS k, to_jsonb(t) AS j
                       FROM %I t
                      WHERE t.ledger_id = $1
                        AND t.id %s $2
                      ORDER BY t.id
                      LIMIT $3
                 )
                 SELECT COALESCE(jsonb_agg(j ORDER BY k), ''[]''::jsonb),
                        count(*)::integer
                   FROM c', p_table, CASE WHEN v_seq = 0 THEN '>=' ELSE '>' END)
            INTO v_chunk, v_rows
            USING p_ledger_id, v_last, p_chunk_rows;

            EXIT WHEN v_rows = 0 AND v_seq > 0;

            INSERT INTO ledger_snapshot_parts (snapshot_id, part_name, seq, content)
            VALUES (p_snapshot_id, p_table, v_seq, v_chunk);

            v_written := v_written + v_rows;
            v_bytes   := v_bytes + octet_length(v_chunk::text);
            v_seq     := v_seq + 1;

            EXIT WHEN v_rows < p_chunk_rows;

            -- The next keyset bound is the last element of the chunk just
            -- ordered by id (Postgres 16 has no max(uuid)).
            v_last := (v_chunk -> -1 ->> 'id')::uuid;
        END LOOP;
    END IF;

    -- Same transaction snapshot as every read above (the caller guarantees
    -- REPEATABLE READ), so the two numbers can only differ if the capture
    -- dropped or duplicated rows.
    IF v_written <> v_count THEN
        RAISE EXCEPTION
            'snapshot capture of % wrote % rows but the ledger has % — refusing an incomplete snapshot',
            p_table, v_written, v_count;
    END IF;

    RETURN v_bytes;
END;
$$;

-- From 247_snapshot_restore_completeness.sql.
CREATE OR REPLACE FUNCTION ledger_snapshot_write(
    p_snapshot_id uuid,
    p_ledger_id   uuid,
    -- 2000 rows/chunk keeps the widest in-scope row type well under ~10 MB per
    -- chunk. Lower it if a future table grows very wide; the cost of a smaller
    -- chunk is more statements, not more memory. Tests pass 1 to put every row
    -- in its own chunk, which is the only way to prove a restore is
    -- order-independent within a table.
    p_chunk_rows  integer DEFAULT 2000
)
RETURNS TABLE (content_size_uncompressed integer)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table  text;
    v_bytes  bigint := 0;
BEGIN
    PERFORM fn_snapshot_require_consistent_read('ledger_snapshot_write');
    IF p_chunk_rows IS NULL OR p_chunk_rows < 1 THEN
        RAISE EXCEPTION 'ledger_snapshot_write: chunk size must be >= 1, got %', p_chunk_rows;
    END IF;

    DELETE FROM ledger_snapshot_parts WHERE snapshot_id = p_snapshot_id;

    FOREACH v_table IN ARRAY fn_ledger_snapshot_part_names() LOOP
        v_bytes := v_bytes + fn_snapshot_write_part(
            p_snapshot_id, p_ledger_id, v_table, p_chunk_rows);
    END LOOP;

    -- Sum of per-chunk renderings rather than one rendering of the whole
    -- document, so this runs a few bytes per chunk larger than the v2 number for
    -- the same data (array brackets and separators). It is a display figure for
    -- the SPA, not a checksum, and measuring it exactly is what cost a full extra
    -- text copy of the document.
    UPDATE ledger_snapshots
       SET content_size_uncompressed = LEAST(v_bytes, 2147483647)::integer
     WHERE id = p_snapshot_id;

    RETURN QUERY SELECT LEAST(v_bytes, 2147483647)::integer;
END;
$$;

-- From 256_fifo_walk_reports_shortfall.sql.
CREATE OR REPLACE FUNCTION public.holdings_fifo_walk(p_account_id uuid, p_security_id uuid, p_as_of timestamp with time zone DEFAULT NULL::timestamp with time zone, p_exclude_header_id uuid DEFAULT NULL::uuid, OUT o_quantity numeric, OUT o_cost_basis numeric, OUT o_lots holdings_fifo_lot[], OUT o_gains holdings_fifo_gain[], OUT o_shortfall numeric)
 RETURNS record
 LANGUAGE plpgsql
 STABLE PARALLEL SAFE
AS $function$
DECLARE
    v_include_fees BOOLEAN;
    v_event        RECORD;
    v_fee          NUMERIC;
    v_remaining    NUMERIC;
    v_consumed     NUMERIC;
    v_take         NUMERIC;
    v_proceeds     NUMERIC;
    v_i            INT;
    v_lot          holdings_fifo_lot;
    -- ADR-0064 ST/LT split (mig 169), accumulated per sell.
    v_consumed_lt  NUMERIC;
    v_qty_lt       NUMERIC;
    v_consumed_qty NUMERIC;
    v_proceeds_lt  NUMERIC;
BEGIN
    o_quantity   := 0;
    o_cost_basis := 0;
    o_lots       := ARRAY[]::holdings_fifo_lot[];
    o_gains      := ARRAY[]::holdings_fifo_gain[];
    o_shortfall  := 0;

    -- Does this brokerage fold trade commissions into basis (mig 056)?
    SELECT COALESCE(b.is_trade_commission, FALSE)
    INTO v_include_fees
    FROM accounts b
    WHERE b.holdings_account_id = p_account_id;
    v_include_fees := COALESCE(v_include_fees, FALSE);

    FOR v_event IN
        SELECT 'leg'::TEXT AS kind,
               hd.posted_at AS event_at,
               hd.action    AS action,
               l.id         AS leg_id,
               l.header_id,
               l.amount,
               l.quantity,
               NULL::NUMERIC AS ratio,
               CASE WHEN l.quantity > 0 THEN 1 ELSE 2 END AS sort_class
        FROM txn_legs l
        JOIN live_txn_headers hd ON hd.id = l.header_id   -- excludes recurring templates (mig 124)
        WHERE l.security_id = p_security_id
          AND l.account_id  = p_account_id
          AND l.quantity   IS NOT NULL
          AND hd.is_hidden  = FALSE
          AND hd.is_merged_into IS NULL                   -- mig 163
          AND (p_as_of IS NULL OR hd.posted_at <= p_as_of)
          AND (p_exclude_header_id IS NULL OR l.header_id <> p_exclude_header_id)  -- mig 256

        UNION ALL

        SELECT 'split'::TEXT, ss.split_at, NULL::TEXT, NULL::UUID, NULL::UUID,
               NULL::NUMERIC, NULL::NUMERIC, ss.ratio, 0
        FROM security_splits ss
        WHERE ss.security_id = p_security_id
          AND (p_as_of IS NULL OR ss.split_at <= p_as_of)

        ORDER BY event_at, sort_class, leg_id
    LOOP
        IF v_event.kind = 'split' THEN
            o_quantity := o_quantity * v_event.ratio;
            -- Quantity scales up, unit_cost down, so lot COST is unchanged — required
            -- where basis = Σ open-lot cost (ADR-0064).
            FOR v_i IN 1 .. COALESCE(array_length(o_lots, 1), 0) LOOP
                IF NOT o_lots[v_i].is_closed THEN
                    o_lots[v_i].quantity := o_lots[v_i].quantity * v_event.ratio;
                    IF v_event.ratio <> 0 THEN
                        o_lots[v_i].unit_cost := o_lots[v_i].unit_cost / v_event.ratio;
                    END IF;
                END IF;
            END LOOP;

        ELSIF v_event.quantity > 0 THEN
            -- Buy / reinvest: one new lot, cost including the fee when folded.
            IF v_include_fees THEN
                v_fee := COALESCE((
                    SELECT SUM(fl.amount) FROM txn_legs fl
                    WHERE fl.header_id = v_event.header_id
                      AND fl.posting_role = 'fee' AND fl.amount > 0), 0);
            ELSE
                v_fee := 0;
            END IF;

            v_lot.leg_id      := v_event.leg_id;
            v_lot.quantity    := v_event.quantity;
            v_lot.unit_cost   := CASE WHEN v_event.quantity = 0 THEN 0
                                      ELSE (v_event.amount + v_fee) / v_event.quantity END;
            v_lot.acquired_at := v_event.event_at;
            v_lot.is_closed   := FALSE;
            o_lots := o_lots || v_lot;

            o_quantity   := o_quantity + v_event.quantity;
            o_cost_basis := o_cost_basis + v_event.amount + v_fee;

        ELSIF v_event.quantity < 0 THEN
            -- Sell: consume lots FIFO. The array is appended in event order, which is
            -- (acquired_at, leg_id) — a stable ordering, unlike mig 148's
            -- ORDER BY acquired_at, <random lot uuid>.
            v_remaining   := ABS(v_event.quantity);
            v_consumed    := 0;
            v_consumed_lt := 0;
            v_qty_lt      := 0;
            FOR v_i IN 1 .. COALESCE(array_length(o_lots, 1), 0) LOOP
                EXIT WHEN v_remaining <= 0;
                CONTINUE WHEN o_lots[v_i].is_closed OR o_lots[v_i].quantity <= 0;

                IF o_lots[v_i].quantity <= v_remaining THEN
                    v_take := o_lots[v_i].quantity;
                    o_lots[v_i].quantity  := 0;
                    o_lots[v_i].is_closed := TRUE;
                ELSE
                    v_take := v_remaining;
                    o_lots[v_i].quantity := o_lots[v_i].quantity - v_remaining;
                END IF;
                v_consumed := v_consumed + v_take * COALESCE(o_lots[v_i].unit_cost, 0);

                -- Long-term iff the sale is more than a year after acquisition.
                -- Splits preserve acquired_at, so the clock runs from the first buy.
                IF v_event.event_at > o_lots[v_i].acquired_at + INTERVAL '1 year' THEN
                    v_consumed_lt := v_consumed_lt + v_take * COALESCE(o_lots[v_i].unit_cost, 0);
                    v_qty_lt      := v_qty_lt + v_take;
                END IF;

                v_remaining := v_remaining - v_take;
            END LOOP;

            -- mig 256: what this disposal could not match. It was dropped here,
            -- silently: o_quantity below still takes the FULL sale, so the
            -- holding goes short, and the gain row books the unmatched shares
            -- at zero cost. Reported so a writer can refuse to make it worse.
            o_shortfall  := o_shortfall + GREATEST(v_remaining, 0);
            o_cost_basis := o_cost_basis - v_consumed;
            o_quantity   := o_quantity + v_event.quantity;

            -- A transfer_shares disposal consumes lots but is NOT a sale, so it
            -- records no realized gain (ADR-0065 D1).
            IF v_event.action IS DISTINCT FROM 'transfer_shares' THEN
                IF v_include_fees THEN
                    v_fee := COALESCE((
                        SELECT SUM(fl.amount) FROM txn_legs fl
                        WHERE fl.header_id = v_event.header_id
                          AND fl.posting_role = 'fee' AND fl.amount > 0), 0);
                ELSE
                    v_fee := 0;
                END IF;
                v_proceeds := (-v_event.amount) - v_fee;

                -- Apportion proceeds to the long-term bucket by consumed-share share.
                -- Multiply before dividing so an exactly divisible split stays exact
                -- (v_proceeds * v_qty_lt / v_consumed_qty is exact whenever the
                -- product divides evenly; taking the share ratio first would round
                -- a repeating fraction such as two-thirds before multiplying).
                v_consumed_qty := ABS(v_event.quantity) - v_remaining;
                IF v_consumed_qty > 0 THEN
                    v_proceeds_lt := v_proceeds * v_qty_lt / v_consumed_qty;
                ELSE
                    v_proceeds_lt := 0;
                END IF;

                o_gains := o_gains || ROW(
                    v_event.leg_id, v_event.event_at, ABS(v_event.quantity),
                    v_proceeds, v_consumed, v_proceeds - v_consumed,
                    v_proceeds_lt, v_consumed_lt,
                    v_proceeds_lt - v_consumed_lt)::holdings_fifo_gain;
            END IF;
        END IF;
    END LOOP;
END;
$function$;

COMMIT;
