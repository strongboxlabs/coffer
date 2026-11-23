-- =============================================================================
-- 262 — re-issue stored text so an upgraded install matches a fresh one
-- =============================================================================
--
-- WHY
-- ---
-- Comments in shipped migrations may be edited (engineering-standards.md §3.1
-- allows it for comments only). As migration 257 sets out, a header comment is
-- gone once the script has run, but two kinds of text are kept by Postgres:
--
--   * COMMENT ON text, in pg_description;
--   * a function's body, comments included, in pg_proc.prosrc.
--
-- When such text is edited in the migration that defined it, a fresh install
-- stores the new text and an upgraded install keeps the old, so the two differ.
-- Re-issued here:
--
--   * fn_ledger_snapshot_payload (function body);
--   * fn_snapshot_write_part (function body);
--   * securities.share_decimals (column comment);
--   * recompute_holdings_cost_basis (function comment);
--   * account_external_ids (table comment);
--   * fn_unbacked_price_dates (function comment);
--   * holdings_fifo_open_lots (function comment).
--
-- Editing them in their original migrations reaches fresh installs only. This
-- migration re-issues each, so an upgraded install ends up holding exactly what
-- a fresh one does.
--
-- WHAT
-- ----
-- Every statement below is copied verbatim from the migration that last defined
-- the object (named above each one). No code changes: each CREATE OR REPLACE has
-- the signature, attributes and body of the current definition, with only its
-- comments rewritten, so grants and ownership carry over untouched.
-- =============================================================================

BEGIN;

-- From 050_widen_share_decimals_ceiling.sql.
COMMENT ON COLUMN securities.share_decimals IS
    'Per-security share-precision (MD `dec` field). Bound to [0,12] '
    '— matches the NUMERIC(25,12) scale of quantity / unit_price / '
    'price columns.';

-- From 257_reissue_stored_comment_text.sql (itself copied from 209).
COMMENT ON FUNCTION recompute_holdings_cost_basis(UUID, UUID, UUID) IS
    'ADR-0064 FIFO basis writer. Since mig 202 a thin persist over holdings_fifo_walk, '
    'so the algorithm is shared with the as-of read path and the two cannot drift. '
    'Bounds what it stores at each column''s OWN scale (money 2dp per mig 182, shares '
    '12dp). Money is rounded once, at its own scale: rounding to 4dp first would round '
    'twice against these NUMERIC(19,2) columns and could shift a disposal by a cent.';

-- From 064_account_external_ids.sql.
COMMENT ON TABLE account_external_ids IS
    'P4 junction (migration 064): maps Ledger accounts to their '
    'source-specific external identifiers. A single Ledger account '
    'can carry one external_id per source (moneydance, simplefin, '
    'manual). The importer''s account-adoption path consults this '
    'table to find an existing account before creating a new one. '
    'Replaces direct keying on accounts.external_id, which supported '
    'only one identity per account, so two sources for one account '
    'produced parallel account rows.';

-- From 241_unbacked_dates_in_one_pass.sql.
COMMENT ON FUNCTION fn_unbacked_price_dates(uuid, uuid) IS
    'Dates on which this security has a trade-source price that no trade '
    'currently derives. One pass over the security''s legs — the per-row '
    'fn_price_is_unbacked rescans them once per price row, a cost that grows '
    'with the length of the price history. '
    'Advisory: such a price may still be correct (ADR-0084 D4).';

-- From 256_fifo_walk_reports_shortfall.sql.
COMMENT ON FUNCTION holdings_fifo_open_lots(uuid, uuid, timestamp with time zone, uuid) IS
    'Mig 256. Open FIFO lots of one (holdings account, security) as of an instant, '
    'optionally without one header — what was available to a sale being edited. '
    'Drives the editor''s lot preview. The lots table is drained in place and holds '
    'only the current position, so the preview reads the walk instead.';

-- From 257_reissue_stored_comment_text.sql (itself copied from 247).
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
    -- this many rows the aggregate is tens of MB — bounded, unlike the
    -- whole-payload aggregate mig 193 set out to eliminate — but it fails rather
    -- than degrading quietly.
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

-- From 258_account_folders_replace_tabs.sql.
CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_payload(p_ledger_id uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 STABLE
AS $function$
DECLARE
    v_result jsonb;
BEGIN
    SELECT jsonb_build_object(
        'accounts',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM accounts t WHERE t.ledger_id = p_ledger_id),
        'securities',                       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM securities t WHERE t.ledger_id = p_ledger_id),
        'security_components',              (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_components t WHERE t.ledger_id = p_ledger_id),
        'account_folders',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM account_folders t WHERE t.ledger_id = p_ledger_id),
        'account_external_ids',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM account_external_ids t WHERE t.ledger_id = p_ledger_id),
        'security_prices',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_prices t WHERE t.ledger_id = p_ledger_id),
        'security_splits',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_splits t WHERE t.ledger_id = p_ledger_id),
        'holdings',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM holdings t WHERE t.ledger_id = p_ledger_id),
        'account_folder_members',           (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM account_folder_members t WHERE t.ledger_id = p_ledger_id),
        'txn_headers',                      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_headers t WHERE t.ledger_id = p_ledger_id),
        'txn_legs',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_legs t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_recon',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_recon t WHERE t.ledger_id = p_ledger_id),
        'lots',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM lots t WHERE t.ledger_id = p_ledger_id),
        -- mig 188: realized_gains is derived, but re-deriving it on restore walked
        -- the whole ledger and overwrote the holdings/lots restored above. Keys on
        -- sell_leg_id (a captured txn_legs row) so it round-trips 1:1.
        'realized_gains',                   (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM realized_gains t WHERE t.ledger_id = p_ledger_id),
        'txn_header_originals',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_originals t WHERE t.ledger_id = p_ledger_id),
        'tags',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM tags t WHERE t.ledger_id = p_ledger_id),
        'txn_header_tags',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_tags t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_tags',                     (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_tags t WHERE t.ledger_id = p_ledger_id),
        'saved_reports',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM saved_reports t WHERE t.ledger_id = p_ledger_id),
        'report_pins',                      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM report_pins t WHERE t.ledger_id = p_ledger_id),
        'provider_security_mappings',       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM provider_security_mappings t WHERE t.ledger_id = p_ledger_id),
        'recurring_transactions',           (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_transactions t WHERE t.ledger_id = p_ledger_id),
        'recurring_occurrence_exceptions',  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_occurrence_exceptions t WHERE t.ledger_id = p_ledger_id),
        'loan_terms',                       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM loan_terms t WHERE t.ledger_id = p_ledger_id),
        'budget_targets',                   (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM budget_targets t WHERE t.ledger_id = p_ledger_id),
        'ledger_operation_promotions',      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM ledger_operation_promotions t WHERE t.ledger_id = p_ledger_id)
    ) INTO v_result;
    RETURN v_result;
END;
$function$;

COMMIT;
