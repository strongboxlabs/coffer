-- 230: flip the header override layer — canonical holds CURRENT, a sidecar holds ORIGINAL.
--
-- ADR-0003 put user edits in txn_header_overrides and read
-- COALESCE(o.<field>, h.<field>), so txn_headers held the feed's values and the
-- override row held yours. Three things went wrong with that in practice.
--
-- 1. A CLEARED FIELD IS UNREPRESENTABLE. The override columns are plain
--    nullable columns with no "is overridden" marker, so NULL has to mean both
--    "not overridden" and "overridden to empty" — and COALESCE resolves it as
--    the former. The bank editor sends null for a cleared payee and the server
--    reads it as "leave alone", so the old value comes back. You cannot clear a
--    payee, memo or check number on a bank row. Ever.
-- 2. THE TWO WRITE PATHS DISAGREED. Investment writes header fields straight
--    onto txn_headers; bank writes the override row. So an investment edit of a
--    row that already carried an override (every merge winner does — the
--    survivor adopts the folded row's date) landed underneath it, where COALESCE
--    could not see it. The user retyped the date, saved successfully, and the
--    register did not move.
-- 3. NOTHING EVER DELETED AN OVERRIDE ROW. Created on first edit and then
--    permanent, so (2) was not a transient state — it was forever.
--
-- THE FLIP. txn_headers now always holds the CURRENT values, and
-- txn_header_originals holds the feed's, captured once on the first edit that
-- changes them. Reads become h.<field> directly — no join, no COALESCE, no
-- ambiguity — and NULL means NULL, so clearing works by construction.
--
-- This is the alternative ADR-0003 rejected as "mutate transactions directly,
-- store original in a sidecar transaction_history". That rejection was aimed at
-- a HISTORY LOG, whose objection ("requires every read to reconstruct history")
-- does not apply to a single original snapshot: recovering the feed value is one
-- row lookup. See the ADR amendment.
--
-- WHY A SIDECAR AND NOT NOTHING. Folding and discarding would have been simpler
-- and would have broken payee recall: GetSimilarPayeesAsync anchors on the RAW
-- payee and suggests the curated one, so it needs both to exist. It now reads
-- the original from here. "Reset to original" and the modified indicator stay
-- possible for the same reason.
--
-- txn_leg_overrides GOES TOO, in the same migration. It holds zero rows in
-- every database and nothing in the API has ever written one, so it is a join
-- on the hottest view and on the balance walk for no rows at all. Splitting it
-- out would have left the schema half-migrated across two releases, which is a
-- worse state than either end.

-- ---------------------------------------------------------------------------
-- 1. The sidecar. Same key, FK and RLS shape as the table it replaces.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS txn_header_originals (
    header_id     UUID PRIMARY KEY REFERENCES txn_headers(id) ON DELETE CASCADE,
    -- Denormalized for RLS, exactly as txn_header_overrides carried it: the
    -- policy gates on this column directly and the composite FK below refuses
    -- any row whose ledger disagrees with its header's.
    ledger_id     UUID NOT NULL,
    payee         TEXT,
    memo          TEXT,
    posted_at     TIMESTAMPTZ,
    transacted_at TIMESTAMPTZ,
    check_number  TEXT,
    -- When the first edit captured this. Diagnostic only.
    captured_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT txn_header_originals_header_ledger_fkey
        FOREIGN KEY (header_id, ledger_id)
        REFERENCES txn_headers(id, ledger_id) ON DELETE CASCADE
);

ALTER TABLE txn_header_originals ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS txn_header_originals_read ON txn_header_originals;
CREATE POLICY txn_header_originals_read ON txn_header_originals
    FOR SELECT TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
         WHERE ulg.user_id = current_app_user_id()));

DROP POLICY IF EXISTS txn_header_originals_write ON txn_header_originals;
CREATE POLICY txn_header_originals_write ON txn_header_originals
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
         WHERE ulg.user_id = current_app_user_id()
           AND ulg.role = ANY (ARRAY['owner'::text, 'editor'::text])))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
         WHERE ulg.user_id = current_app_user_id()
           AND ulg.role = ANY (ARRAY['owner'::text, 'editor'::text])));

GRANT SELECT, INSERT, UPDATE, DELETE ON txn_header_originals TO coffer_app;

-- ---------------------------------------------------------------------------
-- 2 + 3. Move the data: capture the originals, then fold the overrides onto the
--        canonical row.
--
--    GATED ON THE SOURCE TABLE STILL EXISTING, which makes a second run a true
--    no-op rather than a quiet corruption. Everything else in this script is
--    already idempotent (CREATE OR REPLACE, IF NOT EXISTS, DROP ... IF EXISTS),
--    so without this guard a re-run would reach step 2 with txn_header_overrides
--    gone and simply error. Worse, if it did not error: by then txn_headers holds
--    CURATED values, so capturing again would record a later edit's text as the
--    row's "original" — and ON CONFLICT DO NOTHING protects only rows captured on
--    the FIRST pass, not a header edited after the upgrade. Skipping the whole
--    block is the only correct second behaviour.
--
--    A re-run is not hypothetical. DbUp skips a journalled script on restart, but
--    a hand-applied script does not consult the journal, and neither does a
--    restore that brings back the journal row alongside an older schema.
--
--    CAPTURE BEFORE FOLD. txn_headers still holds the feed values at this point,
--    which is precisely what an original is; after the fold they are gone and the
--    override table is dropped, so there is nothing left to recover them from.
--
--    THE FOLD IS COALESCE, NEVER ASSIGNMENT. Most override rows carry only SOME
--    fields — a payee rename sets payee and leaves posted_at NULL — so
--    `SET posted_at = o.posted_at` would blank the date on every one of them.
--    That is the single most dangerous line in this migration.
--
--    is_hidden is deliberately NOT folded. Nothing has ever written
--    txn_header_overrides.is_hidden (hiding writes txn_headers.is_hidden
--    directly), so there is nothing to carry, and folding a column that is
--    always NULL would be a no-op dressed up as a decision.
-- ---------------------------------------------------------------------------
DO $migrate$
BEGIN
    IF to_regclass('public.txn_header_overrides') IS NULL THEN
        RAISE NOTICE 'txn_header_overrides is already gone — 230 has run; skipping the data move.';
        RETURN;
    END IF;

    INSERT INTO txn_header_originals
        (header_id, ledger_id, payee, memo, posted_at, transacted_at, check_number)
    SELECT h.id, h.ledger_id, h.payee, h.memo, h.posted_at, h.transacted_at, h.check_number
      FROM txn_headers h
      JOIN txn_header_overrides o ON o.header_id = h.id
    ON CONFLICT (header_id) DO NOTHING;

    UPDATE txn_headers h
       SET payee         = COALESCE(o.payee,         h.payee),
           memo          = COALESCE(o.memo,          h.memo),
           posted_at     = COALESCE(o.posted_at,     h.posted_at),
           transacted_at = COALESCE(o.transacted_at, h.transacted_at),
           check_number  = COALESCE(o.check_number,  h.check_number)
      FROM txn_header_overrides o
     WHERE o.header_id = h.id;
END
$migrate$;

-- ---------------------------------------------------------------------------
-- 4. Reads come off the canonical row.
--
--    CREATE OR REPLACE, with the column list, types and ORDER byte-identical —
--    so the two register functions that depend on this view's shape
--    (register_filtered_entries RETURNS SETOF resolved_transactions,
--    register_entry_keys) keep working without a DROP ... CASCADE.
--    `has_overrides` keeps its name and type and changes meaning: "this row has
--    an original on file", i.e. it has been edited. Same question, answered from
--    the other side.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE VIEW resolved_transactions AS
 SELECT l.id,
    l.account_id,
    h.payee AS payee,
    COALESCE(l.leg_memo, h.memo) AS memo,
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
    COALESCE(ARRAY( SELECT tg.name
           FROM txn_header_tags tt
             JOIN tags tg ON tg.id = tt.tag_id
          WHERE tt.header_id = h.id
          ORDER BY tg.name), ARRAY[]::text[]) AS tags,
    h.id AS header_id,
    lr.cleared_at,
    lr.cleared_by_user_id,
    l.leg_memo AS leg_memo,
    h.memo AS header_memo,
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
    h.ingest_amount
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

-- security_invoker is the RLS boundary, and CREATE OR REPLACE VIEW does NOT
-- preserve reloptions — nor does pg_get_viewdef emit them, so this is invisible
-- in a diff. Migration 228 records this exact accident turning the view into a
-- cross-ledger leak that only the RLS suite caught. Re-assert it, always.
ALTER VIEW resolved_transactions SET (security_invoker = true);

-- ---------------------------------------------------------------------------
-- 5. The balance walk reads the same way.
--
--    It joined BOTH override tables. The leg-amount join was a no-op on every
--    row in every database (txn_leg_overrides has never held one), and the
--    header join is now redundant because the canonical row holds the effective
--    values.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.balance_walk(p_ledger_id uuid, p_account_id uuid, p_from_posted_at timestamp with time zone, p_starting_balance numeric)
 RETURNS TABLE(account_id uuid, header_id uuid, posted_at timestamp with time zone, seq bigint, net_amount numeric, balance_after numeric)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
    WITH header_net AS (
        SELECT l.account_id,
               -- Carried through the GROUP BY rather than joined again in the outer
               -- query: it is functionally dependent on account_id, and one join to
               -- accounts serves both the ledger filter and the per-account seed.
               COALESCE(a.opening_balance, 0)      AS opening_balance,
               h.id                               AS header_id,
               h.posted_at                        AS posted_at,
               h.seq                              AS seq,
               SUM(l.amount)                      AS net_amount
          FROM live_txn_headers h
          JOIN txn_legs l                   ON l.header_id = h.id
          JOIN accounts a                   ON a.id = l.account_id
         WHERE (p_account_id IS NULL OR l.account_id = p_account_id)
           -- Scoped through accounts, the way mig 188 scoped it, rather than through
           -- txn_legs.ledger_id. The two agree on well-formed data; the account's own
           -- ledger is the authority on which ledger a balance belongs to.
           AND (p_ledger_id IS NULL OR a.ledger_id = p_ledger_id)
           AND h.is_merged_into IS NULL
           AND COALESCE(h.is_hidden, FALSE) = FALSE
           AND h.posted_at >= p_from_posted_at
         GROUP BY l.account_id, COALESCE(a.opening_balance, 0), h.id,
                  h.posted_at, h.seq
    )
    SELECT hn.account_id,
           hn.header_id,
           hn.posted_at,
           hn.seq,
           hn.net_amount,
           -- A given seed wins; NULL means "seed each account from its own opening
           -- balance", which is the whole-ledger-from-the-floor case. NULL rather than
           -- a sentinel because 0 is a legitimate explicit seed.
           COALESCE(p_starting_balance, hn.opening_balance)
               + SUM(hn.net_amount) OVER (
                   PARTITION BY hn.account_id
                   ORDER BY hn.posted_at, hn.seq
                   ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
               ) AS balance_after
      FROM header_net hn;
$function$;

-- ---------------------------------------------------------------------------
-- 6. Snapshot + delete plumbing follows the tables.
--
--    txn_header_overrides becomes txn_header_originals in every list;
--    txn_leg_overrides drops out. Old snapshots are NOT migrated forward: these
--    are in-app restore points, not the backup, and a payload from before this
--    migration restores its (feed-valued) headers without the edits that used to
--    sit beside them. The backup is the recovery path.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_part_names()
 RETURNS text[]
 LANGUAGE sql
 IMMUTABLE
AS $function$
    SELECT ARRAY[
        'accounts', 'securities', 'user_account_groups', 'account_external_ids',
        'security_prices', 'security_splits', 'holdings',
        'user_account_group_members', 'txn_headers', 'txn_legs', 'txn_leg_recon',
        'lots', 'realized_gains', 'txn_header_originals',
        'tags', 'txn_header_tags', 'provider_security_mappings',
        'recurring_transactions', 'recurring_occurrence_exceptions', 'loan_terms',
        'budget_targets'
    ];
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_insert_order()
 RETURNS text[]
 LANGUAGE sql
 IMMUTABLE
AS $function$
    SELECT ARRAY[
        'accounts', 'loan_terms', 'budget_targets', 'securities', 'tags',
        'account_external_ids', 'security_prices', 'security_splits', 'holdings',
        'user_account_groups', 'user_account_group_members',
        'provider_security_mappings', 'recurring_transactions',
        'txn_headers', 'txn_legs', 'lots', 'txn_leg_recon', 'realized_gains',
        'recurring_occurrence_exceptions',
        'txn_header_originals', 'txn_header_tags'
    ];
$function$;

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
        'user_account_groups',              (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM user_account_groups t WHERE t.ledger_id = p_ledger_id),
        'account_external_ids',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM account_external_ids t WHERE t.ledger_id = p_ledger_id),
        'security_prices',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_prices t WHERE t.ledger_id = p_ledger_id),
        'security_splits',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_splits t WHERE t.ledger_id = p_ledger_id),
        'holdings',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM holdings t WHERE t.ledger_id = p_ledger_id),
        'user_account_group_members',       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM user_account_group_members t WHERE t.ledger_id = p_ledger_id),
        'txn_headers',                      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_headers t WHERE t.ledger_id = p_ledger_id),
        'txn_legs',                         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_legs t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_recon',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_recon t WHERE t.ledger_id = p_ledger_id),
        'lots',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM lots t WHERE t.ledger_id = p_ledger_id),
        -- mig 188: realized_gains is derived, but re-deriving it on restore cost
        -- ~27s and overwrote the holdings/lots restored above. Keys on
        -- sell_leg_id (a captured txn_legs row) so it round-trips 1:1.
        'realized_gains',                   (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM realized_gains t WHERE t.ledger_id = p_ledger_id),
        'txn_header_originals',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_originals t WHERE t.ledger_id = p_ledger_id),
        'tags',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM tags t WHERE t.ledger_id = p_ledger_id),
        'txn_header_tags',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_tags t WHERE t.ledger_id = p_ledger_id),
        'provider_security_mappings',       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM provider_security_mappings t WHERE t.ledger_id = p_ledger_id),
        'recurring_transactions',           (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_transactions t WHERE t.ledger_id = p_ledger_id),
        'recurring_occurrence_exceptions',  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_occurrence_exceptions t WHERE t.ledger_id = p_ledger_id),
        'loan_terms',                       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM loan_terms t WHERE t.ledger_id = p_ledger_id),
        'budget_targets',                   (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM budget_targets t WHERE t.ledger_id = p_ledger_id)
    ) INTO v_result;
    RETURN v_result;
END;
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_restore(p_ledger_id uuid, p_payload text)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_payload jsonb := p_payload::jsonb;
BEGIN
    IF v_payload->'realized_gains' IS NULL THEN
        RAISE EXCEPTION
            'snapshot payload has no realized_gains key (pre-mig-188 payload?); '
            'restore would leave realized gains empty. Recapture the snapshot, or '
            'if the schema-version guard was relaxed, restore this payload with a '
            'recompute_holdings_cost_basis(%) pass instead.', p_ledger_id;
    END IF;

    PERFORM fn_ledger_snapshot_clear(p_ledger_id);

    -- ----- Insert rows from the payload (forward-FK order) --------------
    INSERT INTO accounts                   SELECT * FROM jsonb_populate_recordset(NULL::accounts,                   v_payload->'accounts');
    INSERT INTO loan_terms                 SELECT * FROM jsonb_populate_recordset(NULL::loan_terms,                 v_payload->'loan_terms');
    -- COALESCE, unlike its neighbours: a payload captured before mig 225 has no
    -- budget_targets key at all, and restoring it with zero targets is the RIGHT
    -- answer. Contrast the realized_gains guard above, which raises because that
    -- key is derived data whose absence would silently lose rows nothing can
    -- recompute. A missing target is simply a target that was never set.
    INSERT INTO budget_targets             SELECT * FROM jsonb_populate_recordset(NULL::budget_targets,             COALESCE(v_payload->'budget_targets', '[]'::jsonb));
    INSERT INTO securities                 SELECT * FROM jsonb_populate_recordset(NULL::securities,                 v_payload->'securities');
    INSERT INTO tags                       SELECT * FROM jsonb_populate_recordset(NULL::tags,                       v_payload->'tags');
    INSERT INTO account_external_ids       SELECT * FROM jsonb_populate_recordset(NULL::account_external_ids,       v_payload->'account_external_ids');
    INSERT INTO security_prices            SELECT * FROM jsonb_populate_recordset(NULL::security_prices,            v_payload->'security_prices');
    INSERT INTO security_splits            SELECT * FROM jsonb_populate_recordset(NULL::security_splits,            v_payload->'security_splits');
    INSERT INTO holdings                   SELECT * FROM jsonb_populate_recordset(NULL::holdings,                   v_payload->'holdings');
    INSERT INTO user_account_groups        SELECT * FROM jsonb_populate_recordset(NULL::user_account_groups,        v_payload->'user_account_groups');
    INSERT INTO user_account_group_members SELECT * FROM jsonb_populate_recordset(NULL::user_account_group_members, v_payload->'user_account_group_members');
    INSERT INTO provider_security_mappings SELECT * FROM jsonb_populate_recordset(NULL::provider_security_mappings, v_payload->'provider_security_mappings');
    INSERT INTO recurring_transactions     SELECT * FROM jsonb_populate_recordset(NULL::recurring_transactions,     v_payload->'recurring_transactions');
    INSERT INTO txn_headers                SELECT * FROM jsonb_populate_recordset(NULL::txn_headers,                v_payload->'txn_headers');
    INSERT INTO txn_legs                   SELECT * FROM jsonb_populate_recordset(NULL::txn_legs,                   v_payload->'txn_legs');
    INSERT INTO lots                       SELECT * FROM jsonb_populate_recordset(NULL::lots,                       v_payload->'lots');
    INSERT INTO txn_leg_recon              SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_recon,              v_payload->'txn_leg_recon');
    INSERT INTO realized_gains             SELECT * FROM jsonb_populate_recordset(NULL::realized_gains,             v_payload->'realized_gains');
    INSERT INTO recurring_occurrence_exceptions SELECT * FROM jsonb_populate_recordset(NULL::recurring_occurrence_exceptions, v_payload->'recurring_occurrence_exceptions');
    INSERT INTO txn_header_originals       SELECT * FROM jsonb_populate_recordset(NULL::txn_header_originals,       v_payload->'txn_header_originals');
    INSERT INTO txn_header_tags            SELECT * FROM jsonb_populate_recordset(NULL::txn_header_tags,            v_payload->'txn_header_tags');

    -- ----- Pre-230 payloads ---------------------------------------------
    -- A snapshot captured before this migration holds the FEED values in
    -- txn_headers and the user's edits in txn_header_overrides. Restoring it
    -- with that key ignored would reinstate the feed values and silently
    -- discard every edit the ledger had — on the disaster-recovery path, with
    -- no error and nothing on screen to say so. That is a worse failure than
    -- refusing the payload, so the restore folds instead, exactly as this
    -- migration folded the live tables.
    --
    -- The rows just inserted ARE the feed's values, which is what makes the
    -- capture correct and why it has to happen BEFORE the fold. COALESCE, never
    -- assignment: most override rows carry only some fields, and assigning
    -- would blank every column the row leaves NULL.
    --
    -- txn_leg_overrides is deliberately not folded. It held zero rows in every
    -- database and nothing ever wrote one, so there is nothing in any payload
    -- to carry; silently ignoring the key is the whole of the correct
    -- behaviour.
    IF (v_payload->'txn_header_overrides') IS NOT NULL THEN
        INSERT INTO txn_header_originals
            (header_id, ledger_id, payee, memo, posted_at, transacted_at, check_number)
        SELECT h.id, h.ledger_id, h.payee, h.memo, h.posted_at, h.transacted_at, h.check_number
          FROM txn_headers h
         WHERE h.ledger_id = p_ledger_id
           AND h.id IN (SELECT (e->>'header_id')::uuid
                          FROM jsonb_array_elements(v_payload->'txn_header_overrides') e)
        ON CONFLICT (header_id) DO NOTHING;

        UPDATE txn_headers h
           SET payee         = COALESCE(o.payee,         h.payee),
               memo          = COALESCE(o.memo,          h.memo),
               posted_at     = COALESCE(o.posted_at,     h.posted_at),
               transacted_at = COALESCE(o.transacted_at, h.transacted_at),
               check_number  = COALESCE(o.check_number,  h.check_number)
          FROM (SELECT (e->>'header_id')::uuid           AS header_id,
                        e->>'payee'                       AS payee,
                        e->>'memo'                        AS memo,
                       (e->>'posted_at')::timestamptz     AS posted_at,
                       (e->>'transacted_at')::timestamptz AS transacted_at,
                        e->>'check_number'                AS check_number
                  FROM jsonb_array_elements(v_payload->'txn_header_overrides') e) o
         WHERE o.header_id = h.id
           AND h.ledger_id = p_ledger_id;
    END IF;

    PERFORM fn_recompute_balances_for_ledger(p_ledger_id);
END;
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_clear(p_ledger_id uuid)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
BEGIN
    -- loan_terms references accounts; must go before accounts.
    DELETE FROM loan_terms                 WHERE ledger_id = p_ledger_id;
    -- budget_targets references accounts; must go before accounts.
    DELETE FROM budget_targets             WHERE ledger_id = p_ledger_id;
    -- recurring_occurrence_exceptions references recurring_transactions; go first.
    DELETE FROM recurring_occurrence_exceptions WHERE ledger_id = p_ledger_id;
    -- recurring_transactions references accounts AND template headers (mig 183);
    -- must go before both accounts and txn_headers.
    DELETE FROM recurring_transactions     WHERE ledger_id = p_ledger_id;
    -- security_splits references securities; must go before securities.
    DELETE FROM security_splits            WHERE ledger_id = p_ledger_id;
    -- Children of txn_legs first: lots, realized gains, the recon overlay,
    -- override layers, tags.
    DELETE FROM lots                       WHERE ledger_id = p_ledger_id;
    DELETE FROM realized_gains             WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_recon              WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_originals       WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_tags            WHERE ledger_id = p_ledger_id;
    -- Transaction graph.
    DELETE FROM txn_legs                   WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_headers                WHERE ledger_id = p_ledger_id;
    -- Holdings / account-groups / per-security data.
    DELETE FROM user_account_group_members WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_groups        WHERE ledger_id = p_ledger_id;
    DELETE FROM holdings                   WHERE ledger_id = p_ledger_id;
    DELETE FROM security_prices            WHERE ledger_id = p_ledger_id;
    DELETE FROM account_external_ids       WHERE ledger_id = p_ledger_id;
    DELETE FROM provider_security_mappings WHERE ledger_id = p_ledger_id;
    DELETE FROM tags                       WHERE ledger_id = p_ledger_id;
    -- Roots last.
    DELETE FROM securities                 WHERE ledger_id = p_ledger_id;
    DELETE FROM accounts                   WHERE ledger_id = p_ledger_id;
    -- The materialised balance table.
    DELETE FROM txn_header_account_balances WHERE ledger_id = p_ledger_id;
END;
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_delete(p_ledger_id uuid)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
BEGIN
    -- Operational/audit (RESTRICT → ledgers). ledger_operations first so its
    -- CASCADE children (errors + promotions) — whose promotions.header_id
    -- references txn_headers — are gone before the financial block.
    DELETE FROM ledger_operations            WHERE ledger_id = p_ledger_id;

    -- Financial footprint — same child→parent order as the snapshot restore
    -- (db/migrations/127…), kept in lockstep.
    DELETE FROM loan_terms                   WHERE ledger_id = p_ledger_id;
    DELETE FROM recurring_occurrence_exceptions WHERE ledger_id = p_ledger_id;
    DELETE FROM recurring_transactions       WHERE ledger_id = p_ledger_id;
    DELETE FROM security_splits              WHERE ledger_id = p_ledger_id;
    DELETE FROM lots                         WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_originals         WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_tags              WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_legs                     WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_headers                  WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_group_members   WHERE ledger_id = p_ledger_id;
    DELETE FROM user_account_groups          WHERE ledger_id = p_ledger_id;
    DELETE FROM holdings                     WHERE ledger_id = p_ledger_id;
    DELETE FROM security_prices              WHERE ledger_id = p_ledger_id;
    DELETE FROM account_external_ids         WHERE ledger_id = p_ledger_id;
    DELETE FROM provider_security_mappings   WHERE ledger_id = p_ledger_id;
    DELETE FROM tags                         WHERE ledger_id = p_ledger_id;
    DELETE FROM securities                   WHERE ledger_id = p_ledger_id;
    DELETE FROM accounts                     WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_account_balances  WHERE ledger_id = p_ledger_id;

    -- feed_connections (RESTRICT → ledgers; cascades feed_connection_accounts).
    DELETE FROM feed_connections             WHERE ledger_id = p_ledger_id;

    -- The ledger row. CASCADEs user_ledger_grants, ledger_snapshots,
    -- scheduled_jobs, user_preferences.
    DELETE FROM ledgers                      WHERE id = p_ledger_id;
END;
$function$;

-- ---------------------------------------------------------------------------
-- 6b. Everything else that resolved a header field through the override layer.
--
--     All of these read COALESCE(o.<field>, h.<field>) and joined the table to
--     do it. The canonical row now holds that value, so each collapses to
--     h.<field> and loses a join.
--
--     The three holdings functions are here because migration 229 ADDED those
--     joins one release ago — it moved them off the raw date and onto the
--     effective one, which was correct then and is now what the canonical row
--     already is. 229 was not wasted: it fixed the cost-basis mismatch on its
--     own, immediately, without waiting for this.
--
--     account_current_balances is a VIEW and was the one this migration missed
--     on its first dry run — DROP TABLE named it as a dependency. Left in the
--     comment as the reason to trust the clone over the grep.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE VIEW account_current_balances AS
 SELECT a.id AS account_id,
    a.ledger_id,
    a.is_active,
    COALESCE(latest.balance_after, a.opening_balance) AS balance
   FROM accounts a
     LEFT JOIN LATERAL ( SELECT thab.balance_after
           FROM txn_header_account_balances thab
             JOIN txn_headers h ON h.id = thab.header_id
          WHERE thab.account_id = a.id
          ORDER BY (h.posted_at) DESC, h.seq DESC
         LIMIT 1) latest ON true;

ALTER VIEW account_current_balances SET (security_invoker = true);

CREATE OR REPLACE FUNCTION public.account_balance_as_of_instants(p_ledger_id uuid, p_as_ofs timestamp with time zone[], p_account_ids uuid[] DEFAULT NULL::uuid[])
 RETURNS TABLE(as_of timestamp with time zone, account_id uuid, balance numeric)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
WITH asks AS (
    SELECT DISTINCT u AS at FROM unnest(p_as_ofs) AS u WHERE u IS NOT NULL
),
horizon AS (
    SELECT MAX(at) AS t_max FROM asks
),
scoped AS (
    SELECT a.id, a.opening_balance
    FROM accounts a
    WHERE a.ledger_id = p_ledger_id
      AND (p_account_ids IS NULL OR a.id = ANY (p_account_ids))
),
balance_rows AS (
    SELECT thab.account_id,
           h.posted_at AS effective_at,
           h.seq,
           thab.balance_after
    FROM txn_header_account_balances thab
    JOIN txn_headers h ON h.id = thab.header_id
    CROSS JOIN horizon
    WHERE thab.account_id IN (SELECT id FROM scoped)
      AND h.posted_at <= horizon.t_max
),
stream AS (
    SELECT account_id, effective_at, 0 AS is_ask, seq, balance_after,
           NULL::TIMESTAMPTZ AS ask_at
    FROM balance_rows
    UNION ALL
    SELECT s.id, a.at, 1, NULL::BIGINT, NULL::NUMERIC, a.at
    FROM scoped s CROSS JOIN asks a
),
islands AS (
    SELECT account_id, ask_at, balance_after,
           COUNT(balance_after) OVER (
               PARTITION BY account_id ORDER BY effective_at, is_ask, seq NULLS LAST
           ) AS island
    FROM stream
),
filled AS (
    SELECT account_id, ask_at, ff_balance
    FROM (
        SELECT account_id, ask_at,
               MAX(balance_after) OVER (PARTITION BY account_id, island) AS ff_balance
        FROM islands
    ) x
    WHERE x.ask_at IS NOT NULL
)
SELECT f.ask_at AS as_of,
       f.account_id,
       COALESCE(f.ff_balance, s.opening_balance) AS balance
FROM filled f
JOIN scoped s ON s.id = f.account_id;
$function$;

CREATE OR REPLACE FUNCTION public.fn_recompute_balances_for_account(p_account_id uuid, p_from_posted_at timestamp with time zone)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_starting  NUMERIC(19, 4);
    v_ledger_id UUID;
BEGIN
    SELECT a.ledger_id INTO v_ledger_id FROM accounts a WHERE a.id = p_account_id;
    IF v_ledger_id IS NULL THEN
        RETURN;
    END IF;

    -- Seed: the last stored balance strictly before the anchor, else the
    -- account's opening balance.
    SELECT thab.balance_after
      INTO v_starting
      FROM txn_header_account_balances thab
      JOIN live_txn_headers h ON h.id = thab.header_id
     WHERE thab.account_id = p_account_id
       AND h.posted_at < p_from_posted_at
     ORDER BY h.posted_at DESC, h.seq DESC
     LIMIT 1;

    IF v_starting IS NULL THEN
        SELECT a.opening_balance INTO v_starting FROM accounts a WHERE a.id = p_account_id;
    END IF;
    v_starting := COALESCE(v_starting, 0);

    DELETE FROM txn_header_account_balances thab
     USING live_txn_headers h
     WHERE thab.header_id = h.id
       AND thab.account_id = p_account_id
       AND h.posted_at >= p_from_posted_at;

    INSERT INTO txn_header_account_balances (header_id, account_id, ledger_id, balance_after, net_amount)
    SELECT w.header_id, p_account_id, v_ledger_id, w.balance_after, w.net_amount
      FROM balance_walk(NULL, p_account_id, p_from_posted_at, v_starting) w;
END;
$function$;

CREATE OR REPLACE FUNCTION public.ledger_payee_suggestions(p_ledger_id uuid, p_limit integer)
 RETURNS TABLE(name text, count bigint, last_used_at timestamp with time zone)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
    SELECT
        resolved.payee                AS name,
        COUNT(*)                      AS count,
        MAX(resolved.posted_at)       AS last_used_at
    FROM (
        SELECT
            h.payee     AS payee,
            h.is_hidden AS is_hidden,
            h.posted_at
        FROM txn_headers h
        WHERE h.ledger_id        = p_ledger_id
          AND h.is_merged_into   IS NULL
    ) resolved
    WHERE resolved.payee     IS NOT NULL
      AND NOT resolved.is_hidden
    GROUP BY resolved.payee
    ORDER BY count DESC, last_used_at DESC
    LIMIT GREATEST(1, LEAST(p_limit, 10000));
$function$;

CREATE OR REPLACE FUNCTION public.holdings_fifo_walk(p_account_id uuid, p_security_id uuid, p_as_of timestamp with time zone DEFAULT NULL::timestamp with time zone, OUT o_quantity numeric, OUT o_cost_basis numeric, OUT o_lots holdings_fifo_lot[], OUT o_gains holdings_fifo_gain[])
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
                -- (4500 * 10 / 15 = 3000, not 4500 * 0.666... rounded).
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

CREATE OR REPLACE FUNCTION public.holdings_cost_basis_as_of(p_ledger_id uuid, p_as_of timestamp with time zone, p_account_ids uuid[] DEFAULT NULL::uuid[])
 RETURNS TABLE(account_id uuid, security_id uuid, quantity numeric, cost_basis numeric)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
    -- Positions are discovered from the LEGS, not from the holdings projection: a
    -- position closed since p_as_of has no projection row but was held then. The
    -- qty <> 0 filter drops anything closed BY p_as_of, matching the valuation feeder.
    SELECT p.account_id,
           p.security_id,
           ROUND(w.o_quantity, 12)  AS quantity,
           ROUND(w.o_cost_basis, 4) AS cost_basis
    FROM (
        SELECT DISTINCT l.account_id, l.security_id
        FROM txn_legs l
        JOIN live_txn_headers h ON h.id = l.header_id
        WHERE l.ledger_id     = p_ledger_id
          AND l.security_id  IS NOT NULL
          AND l.quantity     IS NOT NULL
          AND h.is_hidden     = FALSE
          AND h.posted_at <= p_as_of
          AND (p_account_ids IS NULL OR l.account_id = ANY (p_account_ids))
    ) p
    CROSS JOIN LATERAL holdings_fifo_walk(p.account_id, p.security_id, p_as_of) w
    WHERE w.o_quantity <> 0;
$function$;

CREATE OR REPLACE FUNCTION public.holdings_market_value_as_of_set(p_ledger_id uuid, p_as_ofs timestamp with time zone[], p_account_ids uuid[] DEFAULT NULL::uuid[])
 RETURNS TABLE(as_of timestamp with time zone, account_id uuid, security_id uuid, quantity numeric, market_value numeric, priced_from text)
 LANGUAGE sql
 STABLE PARALLEL SAFE
AS $function$
WITH asks AS (
    SELECT DISTINCT u AS at
    FROM unnest(p_as_ofs) AS u
    WHERE u IS NOT NULL
),
horizon AS (
    SELECT MAX(at) AS t_max FROM asks
),
-- Every holdings-side leg up to the LAST requested instant. Discovery is bounded
-- by t_max rather than per instant; a position not yet started at an earlier
-- instant accumulates to zero there and is dropped by the qty <> 0 filter, exactly
-- as mig 172 skips it.
raw_legs AS (
    SELECT l.account_id,
           l.security_id,
           l.id           AS leg_id,
           h.posted_at AS posted_at,
           l.quantity,
           CASE WHEN l.quantity > 0 THEN 1 ELSE 2 END AS sort_class
    FROM txn_legs l
    JOIN live_txn_headers h ON h.id = l.header_id
    CROSS JOIN horizon
    WHERE l.ledger_id      = p_ledger_id
      AND l.security_id   IS NOT NULL
      AND l.quantity      IS NOT NULL
      AND h.posted_at     <= horizon.t_max
      AND h.is_hidden      = FALSE
      AND h.is_merged_into IS NULL
      AND (p_account_ids IS NULL OR l.account_id = ANY (p_account_ids))
),
positions AS (
    SELECT DISTINCT account_id, security_id FROM raw_legs
),
held_securities AS (
    SELECT DISTINCT security_id FROM raw_legs
),
-- Splits per security as parallel arrays, bounded to t_max. Tiny by nature, and it
-- makes every per-leg and per-instant factor an array unnest rather than a probe.
split_arr AS (
    SELECT ss.security_id,
           array_agg(ss.ratio    ORDER BY ss.split_at) AS ratios,
           array_agg(ss.split_at ORDER BY ss.split_at) AS split_ats
    FROM security_splits ss
    CROSS JOIN horizon
    WHERE ss.ledger_id = p_ledger_id
      AND ss.split_at <= horizon.t_max
    GROUP BY ss.security_id
),
-- (1) Restate each leg into t_max's split basis.
adjusted AS (
    SELECT rl.account_id,
           rl.security_id,
           rl.leg_id,
           rl.posted_at,
           rl.sort_class,
           rl.quantity * numeric_product(
               CASE WHEN sa.ratios IS NULL THEN NULL::NUMERIC[]
               ELSE ARRAY(
                   SELECT r FROM unnest(sa.ratios, sa.split_ats) AS u(r, sat)
                   WHERE sat > rl.posted_at)
               END) AS adj_quantity
    FROM raw_legs rl
    LEFT JOIN split_arr sa ON sa.security_id = rl.security_id
),
-- Legs and instants in one stream. sort_class 9 puts an instant after every real
-- leg sharing it, which is what `posted_at <= p_as_of` means.
qty_stream AS (
    SELECT account_id, security_id, posted_at AS event_at, sort_class, leg_id,
           adj_quantity, NULL::TIMESTAMPTZ AS ask_at
    FROM adjusted
    UNION ALL
    SELECT p.account_id, p.security_id, a.at, 9, NULL::UUID, 0::NUMERIC, a.at
    FROM positions p
    CROSS JOIN asks a
),
qty_running AS (
    SELECT s.account_id, s.security_id, s.ask_at,
           SUM(s.adj_quantity) OVER (
               PARTITION BY s.account_id, s.security_id
               ORDER BY s.event_at, s.sort_class, s.leg_id NULLS FIRST
           ) AS cum_adj_quantity
    FROM qty_stream s
),
-- Read the running total at each instant and divide out the splits not yet due.
held AS (
    SELECT r.ask_at AS at,
           r.account_id,
           r.security_id,
           r.cum_adj_quantity
               / NULLIF(numeric_product(
                     CASE WHEN sa.ratios IS NULL THEN NULL::NUMERIC[]
                     ELSE ARRAY(
                         SELECT rr FROM unnest(sa.ratios, sa.split_ats) AS u(rr, sat)
                         WHERE sat > r.ask_at)
                     END), 0) AS quantity
    FROM qty_running r
    LEFT JOIN split_arr sa ON sa.security_id = r.security_id
    WHERE r.ask_at IS NOT NULL
),
holdings_at AS (
    SELECT * FROM held WHERE quantity IS NOT NULL AND quantity <> 0
),
-- (2) Feed price by forward fill, at DATE granularity to match mig 172.
ask_dates AS (
    SELECT at, at::date AS on_date FROM asks
),
feed_stream AS (
    SELECT sp.security_id, sp.price_date AS on_date, 0 AS is_ask,
           sp.price, NULL::TIMESTAMPTZ AS ask_at
    FROM security_prices sp
    CROSS JOIN horizon
    WHERE sp.ledger_id   = p_ledger_id
      AND sp.price_date <= horizon.t_max::date
      AND sp.security_id IN (SELECT security_id FROM held_securities)
    UNION ALL
    SELECT hs.security_id, ad.on_date, 1, NULL::NUMERIC, ad.at
    FROM held_securities hs
    CROSS JOIN ask_dates ad
),
feed_islands AS (
    SELECT security_id, on_date, is_ask, price, ask_at,
           COUNT(price) OVER (
               PARTITION BY security_id ORDER BY on_date, is_ask
           ) AS island
    FROM feed_stream
),
feed_at AS (
    SELECT security_id, ask_at, ff_price, ff_date
    FROM (
        SELECT security_id, ask_at,
               FIRST_VALUE(price)   OVER w AS ff_price,
               FIRST_VALUE(on_date) OVER w AS ff_date
        FROM feed_islands
        WINDOW w AS (PARTITION BY security_id, island ORDER BY on_date, is_ask)
    ) x
    WHERE x.ask_at IS NOT NULL
),
needs_trade AS (
    SELECT h.at, h.account_id, h.security_id
    FROM holdings_at h
    LEFT JOIN feed_at f ON f.security_id = h.security_id AND f.ask_at = h.at
    WHERE f.ff_price IS NULL
),
-- (3) Trade price by the same fill, per (account, security). Each price row opens
-- its own island, so an instant sharing a timestamp with several trades takes the
-- last of them — mig 172's ORDER BY posted_at DESC, id DESC.
trade_stream AS (
    SELECT l.account_id, l.security_id, hh.posted_at AS at, 0 AS is_ask,
           l.unit_price, l.id AS leg_id, NULL::TIMESTAMPTZ AS ask_at
    FROM txn_legs l
    JOIN live_txn_headers hh ON hh.id = l.header_id
    CROSS JOIN horizon
    WHERE l.ledger_id      = p_ledger_id
      AND l.security_id   IS NOT NULL
      AND l.unit_price    IS NOT NULL
      AND hh.posted_at    <= horizon.t_max
      AND hh.is_hidden     = FALSE
      AND hh.is_merged_into IS NULL
      AND (p_account_ids IS NULL OR l.account_id = ANY (p_account_ids))
    UNION ALL
    -- Only the (position, instant) pairs that MISSED a feed close ask for a trade
    -- price. On a ledger with dense feed data that is nothing, so the whole stream
    -- collapses to the price rows and the sort is trivial; without the gate this
    -- built and sorted a full positions x instants cross join to answer nobody.
    SELECT n.account_id, n.security_id, n.at, 1, NULL::NUMERIC, NULL::UUID, n.at
    FROM needs_trade n
),
trade_islands AS (
    SELECT account_id, security_id, at, is_ask, unit_price, leg_id, ask_at,
           COUNT(unit_price) OVER (
               PARTITION BY account_id, security_id ORDER BY at, is_ask, leg_id NULLS FIRST
           ) AS island
    FROM trade_stream
),
trade_at AS (
    SELECT account_id, security_id, ask_at, ff_price, ff_at
    FROM (
        SELECT account_id, security_id, ask_at,
               FIRST_VALUE(unit_price) OVER w AS ff_price,
               FIRST_VALUE(at)         OVER w AS ff_at
        FROM trade_islands
        WINDOW w AS (
            PARTITION BY account_id, security_id, island
            ORDER BY at, is_ask, leg_id NULLS FIRST
        )
    ) x
    WHERE x.ask_at IS NOT NULL
),
resolved AS (
    SELECT h.at,
           h.account_id,
           h.security_id,
           h.quantity,
           COALESCE(f.ff_price, t.ff_price) AS obs_price,
           -- A same-day split is already reflected in that day's close, so a feed
           -- observation's boundary is the start of the following day.
           CASE WHEN f.ff_price IS NOT NULL
                THEN (f.ff_date + 1)::timestamptz
                ELSE t.ff_at
           END AS obs_at,
           CASE WHEN f.ff_price IS NOT NULL THEN 'feed'
                WHEN t.ff_price IS NOT NULL THEN 'trade'
                ELSE 'none'
           END AS priced_from
    FROM holdings_at h
    LEFT JOIN feed_at f
           ON f.security_id = h.security_id AND f.ask_at = h.at
    LEFT JOIN trade_at t
           ON t.account_id  = h.account_id
          AND t.security_id  = h.security_id
          AND t.ask_at       = h.at
)
-- Back-adjust the observed per-share price onto the instant's split basis: a price
-- seen before a split that has since happened is on the pre-split basis, and a
-- split-adjusted quantity times a raw price would count the split twice. ROUND
-- bounds the NUMERIC scale — unbounded division overflows System.Decimal.
SELECT r.at AS as_of,
       r.account_id,
       r.security_id,
       ROUND(r.quantity, 12) AS quantity,
       ROUND(r.quantity * COALESCE(
           CASE
               WHEN r.obs_price IS NULL OR r.obs_at IS NULL THEN r.obs_price
               ELSE (
                   SELECT CASE
                       WHEN f.factor > 0 AND f.factor <> 1
                       THEN ROUND(r.obs_price / f.factor, 12)
                       ELSE r.obs_price
                   END
                   FROM (
                       SELECT numeric_product(
                           CASE WHEN sa.ratios IS NULL THEN NULL::NUMERIC[]
                           ELSE ARRAY(
                               SELECT rr FROM unnest(sa.ratios, sa.split_ats) AS u(rr, sat)
                               WHERE sat > r.obs_at AND sat <= r.at)
                           END) AS factor
                       FROM (SELECT 1) one
                       LEFT JOIN split_arr sa ON sa.security_id = r.security_id
                   ) f
               )
           END, 0), 4) AS market_value,
       r.priced_from
FROM resolved r;
$function$;

-- ---------------------------------------------------------------------------
-- 7. The tables go.
--
--    txn_leg_overrides held zero rows in every database, and nothing in the API
--    ever wrote one — its only mentions were a delete, an ANALYZE list and the
--    COALESCEs just removed. txn_header_overrides has been folded above.
-- ---------------------------------------------------------------------------
DROP TABLE IF EXISTS txn_leg_overrides;
DROP TABLE IF EXISTS txn_header_overrides;
