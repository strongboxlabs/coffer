-- =============================================================================
-- 266: resolved_transactions loses its own memo and tag columns (memo, tags,
-- leg_memo, header_memo), live_txn_headers and template_txn_headers lose memo,
-- and the register's SQL functions (register_entry_keys,
-- register_filtered_entries) are dropped.
-- =============================================================================
--
-- WHY. What a row shows as its memo and tags is the memo-and-tags rule's output,
-- which the API computes when it writes and stores per leg (264's notes_* columns
-- and txn_leg_note_tags); every reader reads that. The view's old columns were a
-- second, different definition (a raw COALESCE of the leg's and the header's memo,
-- the header's tags on every leg), and the two register functions filtered and
-- keyed entries on them. A reader that used either would show what the rule does
-- not, so neither stays where a reader could reach it. The raw stored inputs stay
-- on the base tables for the writers and the recompute.
--
-- A FUNCTION THAT READS A DROPPED COLUMN. PostgreSQL tracks a view's dependents and
-- a BEGIN ATOMIC body, so those stop a DROP VIEW on their own. A string-bodied SQL
-- or PL/pgSQL function is not tracked and would fail only when called. The scan
-- below refuses the upgrade, naming each one, when a function's source names one of
-- the three views together with one of the dropped columns. It is deliberately
-- coarse: a function it names is re-issued here without the column, never left
-- behind.
--
-- RECREATED, NOT REPLACED. CREATE OR REPLACE VIEW cannot drop a column, so the
-- three views are dropped and created again: resolved_transactions reads
-- live_txn_headers, so it goes first and comes back last. Each definition is the
-- live one at 264 with the dropped columns left out and nothing else changed.
-- security_invoker is the RLS boundary and a new view does not carry it, so it is
-- set on all three; 254 revoked the default grants, so coffer_app's SELECT is
-- granted again explicitly.
-- =============================================================================

BEGIN;

DO $$
DECLARE
    v_functions text;
BEGIN
    SELECT string_agg(p.oid::regprocedure::text, ', ' ORDER BY p.oid::regprocedure::text)
      INTO v_functions
      FROM pg_proc p
      JOIN pg_namespace n ON n.oid = p.pronamespace
     WHERE n.nspname = 'public'
       AND p.proname NOT IN ('register_entry_keys', 'register_filtered_entries')
       AND p.prosrc ~ '\m(resolved_transactions|live_txn_headers|template_txn_headers)\M'
       AND p.prosrc ~ '\m(memo|tags|leg_memo|header_memo)\M';
    IF v_functions IS NOT NULL THEN
        RAISE EXCEPTION 'migration 266: these functions name a view whose memo or tag columns 266 drops: %', v_functions
            USING HINT = 'Re-issue each one in 266 without the column, then rerun the upgrade.';
    END IF;
END $$;

-- Full signatures, as 226 created them, so no overload survives. IF EXISTS keeps a
-- rerun of the script (a journal row lost, a restore onto a migrated database)
-- harmless.
DROP FUNCTION IF EXISTS register_entry_keys(
    uuid[], uuid, uuid, bigint, text, integer, boolean, text, date, date,
    numeric, numeric, uuid, text, uuid, text, date, text, text);
DROP FUNCTION IF EXISTS register_filtered_entries(
    uuid[], uuid, boolean, text, date, date, numeric, numeric, uuid, text,
    uuid, text, date);

DROP VIEW IF EXISTS resolved_transactions;
DROP VIEW IF EXISTS live_txn_headers;
DROP VIEW IF EXISTS template_txn_headers;

CREATE VIEW live_txn_headers AS
 SELECT id,
    ledger_id,
    origin,
    external_id,
    payee,
    posted_at,
    transacted_at,
    check_number,
    is_pending,
    is_hidden,
    is_merged_into,
    import_source,
    created_at,
    online_match_fitid,
    online_match_fi_id,
    needs_review,
    action,
    ingest_action_hint,
    provider_raw_payload,
    seq,
    provider_key,
    is_merge_winner,
    ingest_shares,
    ingest_unit_price,
    ingest_fee,
    ingest_security_ticker_hint,
    is_recurring_template,
    recurring_transaction_id,
    occurrence_date,
    ingest_amount
   FROM txn_headers
  WHERE NOT is_recurring_template;

CREATE VIEW template_txn_headers AS
 SELECT id,
    ledger_id,
    origin,
    external_id,
    payee,
    posted_at,
    transacted_at,
    check_number,
    is_pending,
    is_hidden,
    is_merged_into,
    import_source,
    created_at,
    online_match_fitid,
    online_match_fi_id,
    needs_review,
    action,
    ingest_action_hint,
    provider_raw_payload,
    seq,
    provider_key,
    is_merge_winner,
    ingest_shares,
    ingest_unit_price,
    ingest_fee,
    ingest_security_ticker_hint,
    is_recurring_template,
    recurring_transaction_id,
    occurrence_date
   FROM txn_headers
  WHERE is_recurring_template;

CREATE VIEW resolved_transactions AS
 SELECT l.id,
    l.account_id,
    h.payee AS payee,
    l.amount AS amount,
    h.posted_at AS posted_at,
    h.transacted_at AS transacted_at,
    COALESCE(lr.status, 'uncleared'::text) AS status,
    COALESCE(h.is_hidden, false) AS is_hidden,
    orig.header_id IS NOT NULL AS has_overrides,
    thab.balance_after,
    h.origin,
    h.is_pending,
    h.is_merged_into,
    h.action AS investment_action,
    h.external_id,
    l.created_at,
    h.check_number AS check_number,
    other.id AS counterparty_id,
        CASE
            WHEN l.header_total_postings > 1 THEN h.id
            ELSE NULL::uuid
        END AS txn_group_id,
    l.posting_index AS leg_index,
    other.account_id AS counterparty_account_id,
    account_path(other.account_id) AS counterparty_account_name,
    ca.account_type AS counterparty_account_type,
    h.id AS header_id,
    lr.cleared_at,
    lr.cleared_by_user_id,
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
    COALESCE(h.action,
        CASE
            WHEN this_account.account_type <> 'category'::text AND ca.account_type IS NOT NULL AND ca.account_type <> 'category'::text THEN 'Xfr'::text
            ELSE NULL::text
        END) AS derived_action,
    this_account.account_type,
    h.ingest_amount,
    l.notes_kind,
    l.notes_is_read_only,
    l.notes_groups_by_header,
    l.notes_memo,
    l.notes_memo_inherited,
    l.notes_inherited_memo,
    l.notes_inherit_from_account_id,
    l.notes_tags_inherited,
    l.notes_header_memo,
    l.notes_header_in_play,
    other.notes_kind AS counterparty_notes_kind,
    other.notes_is_read_only AS counterparty_notes_is_read_only,
    other.notes_groups_by_header AS counterparty_notes_groups_by_header,
    other.notes_memo AS counterparty_notes_memo,
    other.notes_memo_inherited AS counterparty_notes_memo_inherited,
    other.notes_inherited_memo AS counterparty_notes_inherited_memo,
    other.notes_inherit_from_account_id AS counterparty_notes_inherit_from_account_id,
    other.notes_tags_inherited AS counterparty_notes_tags_inherited,
    other.notes_header_memo AS counterparty_notes_header_memo,
    other.notes_header_in_play AS counterparty_notes_header_in_play
   FROM txn_legs l
     JOIN live_txn_headers h ON h.id = l.header_id
     JOIN accounts this_account ON this_account.id = l.account_id
     LEFT JOIN txn_header_originals orig ON orig.header_id = h.id
     LEFT JOIN txn_leg_recon lr ON lr.leg_id = l.id
     LEFT JOIN txn_legs other ON other.header_id = l.header_id AND other.posting_index = l.posting_index AND other.id <> l.id
     LEFT JOIN accounts ca ON ca.id = other.account_id
     LEFT JOIN securities s ON s.id = COALESCE(l.security_id, other.security_id)
     LEFT JOIN txn_header_account_balances thab ON thab.header_id = h.id AND thab.account_id = l.account_id
     LEFT JOIN provider_security_mappings psm ON psm.ledger_id = h.ledger_id AND psm.provider_key = h.provider_key AND psm.provider_security_id = h.ingest_security_ticker_hint;

ALTER VIEW live_txn_headers      SET (security_invoker = true);
ALTER VIEW template_txn_headers  SET (security_invoker = true);
ALTER VIEW resolved_transactions SET (security_invoker = true);

GRANT SELECT ON live_txn_headers, template_txn_headers, resolved_transactions TO coffer_app;

COMMIT;
