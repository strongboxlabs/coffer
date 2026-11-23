-- =============================================================================
-- 264 — memo and tags per side: the stored presentation, and the annotation data
-- =============================================================================
--
-- One transaction (BEGIN ... COMMIT below): a failure anywhere leaves the database
-- exactly as it was and journals nothing, so the next start runs the whole script
-- again.
--
-- Two halves. The first creates the stored presentation (below); the second, THE
-- DATA STEPS at the end, moves every existing memo and tag to where the per-side rule
-- reads it, merges tags that differ only by case, and checks the invariants. The C#
-- step after the script then stores what the rule shows for every leg.
--
-- What a register shows for a leg's memo and tags is decided by one rule, written
-- once in C# (MemoTagRules). Evaluated at read time it needs, for every row, the
-- posting's other leg, the investment side through the holdings links, the
-- originating account and has-tags facts from three junction tables; every page,
-- filter, search and report pays for them. What the rule produces is stored
-- instead, computed by that same C# rule whenever a writer changes one of its
-- inputs (LegNotes, called from the save interceptor and from the set-based
-- writers, in the writer's own transaction). Readers read it as plain columns of
-- the row they already have.
--
-- NO SQL DEFINITION OF THE RULE EXISTS. Nothing here, and no function, derives a
-- value: the columns are the C# rule's output. A C# step that runs directly after
-- this script (LegNotesBackfill, journaled as 264_stored_leg_notes_backfill) fills
-- them for existing data, from the stored rows alone, so it is deterministic.
--
--   txn_legs.notes_*   the rule's presentation of the leg from its own account
--                      (kind, read-only, entry grouping, the memo it shows and the
--                      one it would inherit, where the inherited value comes from,
--                      the transaction's memo where the header is in play). On the
--                      leg row, so every reader that reads the leg reads them with
--                      no further join; resolved_transactions exposes them for the
--                      row's leg and, through the other-leg join it already has,
--                      for the posting's other leg.
--   txn_leg_note_tags  one row per (leg, tag) the presentation names, with the roles
--                      the tag plays: shown, in play (filters and reports), inherited,
--                      the transaction's. Tags by id, so a rename touches nothing.
--
-- The columns are NULL until the rule has run for the leg. Every writer fills them
-- in the transaction that writes the leg (or inserts the leg with them already
-- set), so an unfilled leg is a state no writer leaves behind; the check below makes
-- a leg all or nothing, so a partly written presentation cannot exist. Nothing but
-- LegNotes writes them, and the backfill step through a store of its own that maps them
-- exactly as this script creates them (StoredNotesWriteCatalogTests).
--
-- SNAPSHOTS CAPTURE BOTH and a restore copies them back verbatim, with no
-- recompute: the notes_* columns ride along with the txn_legs rows, and
-- txn_leg_note_tags is a captured table of its own, inserted after the legs and the
-- tags. That is sound because a restore refuses any snapshot whose schema version
-- differs from the live database's, so a restored row was computed by the rule this
-- database runs; a later change of the rule ships as a recompute step, which also
-- retires the older snapshots. The tag rows cascade from their leg and tag; a writer
-- that changes what a remaining leg shows recomputes the header.
--
-- THE SNAPSHOT WRITER CHUNKS EVERY TABLE ON ITS PRIMARY KEY. fn_snapshot_write_part
-- keyset-chunked only a table with a column named id and wrote every other one as
-- a single aggregate under a row ceiling. The tag-role table is about as large as
-- txn_legs, so writing it in one piece would hold the whole table in one value in
-- the backend that already runs closest to its memory limit. The writer now reads
-- each table's primary key from the catalog, whatever its shape, and chunks on it;
-- a captured table without one is refused. The one-piece path and its ceiling are
-- gone.
--
-- The ledger_id column and the composite foreign keys of txn_leg_note_tags follow
-- txn_leg_tags (243): the flat RLS predicate is then a consequence, not an
-- assertion. Members read; owners and editors write, as every write of ledger
-- data (254).
-- =============================================================================

BEGIN;

-- RERUNNABLE OVER ITS OWN RESULT. The runner journals the script in a statement of its
-- own after the COMMIT below, so a stop between the two leaves everything here applied
-- and nothing journaled, and the next start runs the whole script again. Every object
-- is therefore created only where it is missing (a policy is dropped and created, so
-- its definition is always this one); everything else is CREATE OR REPLACE, a grant, a
-- comment or a guard, which a second run repeats harmlessly. The data steps hold to
-- the same (see there).

ALTER TABLE txn_legs
    ADD COLUMN IF NOT EXISTS notes_kind                    text    NULL,
    ADD COLUMN IF NOT EXISTS notes_is_read_only            boolean NULL,
    ADD COLUMN IF NOT EXISTS notes_groups_by_header        boolean NULL,
    ADD COLUMN IF NOT EXISTS notes_memo                    text    NULL,
    ADD COLUMN IF NOT EXISTS notes_memo_inherited          boolean NULL,
    ADD COLUMN IF NOT EXISTS notes_inherited_memo          text    NULL,
    ADD COLUMN IF NOT EXISTS notes_inherit_from_account_id uuid    NULL,
    ADD COLUMN IF NOT EXISTS notes_tags_inherited          boolean NULL,
    ADD COLUMN IF NOT EXISTS notes_header_memo             text    NULL,
    ADD COLUMN IF NOT EXISTS notes_header_in_play          boolean NULL;

DO $ck$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                    WHERE conname = 'ck_txn_legs_notes_kind'
                      AND conrelid = 'public.txn_legs'::regclass) THEN
        ALTER TABLE txn_legs
            ADD CONSTRAINT ck_txn_legs_notes_kind CHECK (notes_kind IN (
                'OnePosting', 'EventCounterpartOne', 'SplitSide', 'SplitOpposite', 'EventSide', 'EventOpposite'));
    END IF;
    -- Presented or not, never half: the kind and every flag are set together.
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                    WHERE conname = 'ck_txn_legs_notes_whole'
                      AND conrelid = 'public.txn_legs'::regclass) THEN
        ALTER TABLE txn_legs
            ADD CONSTRAINT ck_txn_legs_notes_whole CHECK (
                (notes_kind IS NULL
                 AND notes_is_read_only IS NULL AND notes_groups_by_header IS NULL
                 AND notes_memo_inherited IS NULL AND notes_tags_inherited IS NULL
                 AND notes_header_in_play IS NULL
                 AND notes_memo IS NULL AND notes_inherited_memo IS NULL
                 AND notes_inherit_from_account_id IS NULL AND notes_header_memo IS NULL)
                OR
                (notes_kind IS NOT NULL
                 AND notes_is_read_only IS NOT NULL AND notes_groups_by_header IS NOT NULL
                 AND notes_memo_inherited IS NOT NULL AND notes_tags_inherited IS NOT NULL
                 AND notes_header_in_play IS NOT NULL));
    END IF;
END $ck$;

COMMENT ON COLUMN txn_legs.notes_kind IS
    'The memo-and-tags rule''s presentation of the leg from its own account (with the other '
    'notes_* columns), as the C# rule computed it when its inputs were last written; NULL until '
    'it has run. Derived; written only by LegNotes and the upgrade''s backfill step.';

CREATE TABLE IF NOT EXISTS txn_leg_note_tags (
    leg_id            uuid    NOT NULL,
    tag_id            uuid    NOT NULL,
    header_id         uuid    NOT NULL,
    ledger_id         uuid    NOT NULL,
    shown             boolean NOT NULL,
    in_play           boolean NOT NULL,
    inherited         boolean NOT NULL,
    transaction_level boolean NOT NULL,

    CONSTRAINT pk_txn_leg_note_tags PRIMARY KEY (leg_id, tag_id),
    CONSTRAINT fk_txn_leg_note_tags_leg
        FOREIGN KEY (leg_id, ledger_id) REFERENCES txn_legs(id, ledger_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_txn_leg_note_tags_tag
        FOREIGN KEY (tag_id, ledger_id) REFERENCES tags(id, ledger_id)
        ON DELETE CASCADE
);

COMMENT ON TABLE txn_leg_note_tags IS
    'The tags the stored presentation of a leg names, by id, with the roles each plays. '
    'Derived; written only by LegNotes and the upgrade''s backfill step.';

-- A recompute reads a header's tag rows to compare them with what it computes.
CREATE INDEX IF NOT EXISTS ix_txn_leg_note_tags_header ON txn_leg_note_tags (header_id);
-- A tag filter and a tag report start from the tag: which legs count it.
CREATE INDEX IF NOT EXISTS ix_txn_leg_note_tags_tag_in_play ON txn_leg_note_tags (tag_id, leg_id) WHERE in_play;
-- Deleting a tag cascades here on (tag_id, ledger_id), across every role: the partial
-- index above cannot serve that, and without this each deleted tag would scan the
-- table. As txn_leg_tags (243). A leg's cascade is served by the primary key.
CREATE INDEX IF NOT EXISTS ix_txn_leg_note_tags_ledger_tag ON txn_leg_note_tags (ledger_id, tag_id);

ALTER TABLE txn_leg_note_tags ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS txn_leg_note_tags_read ON txn_leg_note_tags;
DROP POLICY IF EXISTS txn_leg_note_tags_write ON txn_leg_note_tags;
CREATE POLICY txn_leg_note_tags_read ON txn_leg_note_tags FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()));
CREATE POLICY txn_leg_note_tags_write ON txn_leg_note_tags FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));

-- A row whose roles or header change is deleted and inserted again: no UPDATE.
GRANT SELECT, INSERT, DELETE ON txn_leg_note_tags TO coffer_app;

-- ---------------------------------------------------------------------------
-- A recompute locks its headers before it reads the rule's inputs, so two recomputes
-- of one header run one after the other. Two recomputes over overlapping sets of
-- headers (a bulk writer each) must take those locks in the same order, or each can
-- hold a header the other waits for. An UPDATE locks rows in whatever order its plan
-- visits them; a locking SELECT with ORDER BY locks them in that order, because the
-- rows are sorted before they are locked. Hence a function: LINQ has no locking read.
-- FOR NO KEY UPDATE, the lock an UPDATE of a non-key column takes: recomputes exclude
-- each other, and a leg inserted under the header (a key-share lock) is not blocked.
--
-- READ COMMITTED ONLY. The recompute reads the rule's inputs after the lock, and sees
-- what the previous holder committed only because each READ COMMITTED statement takes
-- a fresh snapshot. A REPEATABLE READ or SERIALIZABLE transaction keeps the snapshot it
-- took before the lock was granted, so a recompute in one would store values computed
-- from rows another writer has since replaced. Refused here rather than trusted to
-- every caller.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_lock_txn_headers(p_header_ids uuid[])
RETURNS TABLE (locked integer)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION
            'fn_lock_txn_headers must run in a READ COMMITTED transaction, not %: a recompute '
            'reads its inputs after the lock, which only READ COMMITTED sees as committed',
            current_setting('transaction_isolation')
            USING ERRCODE = 'invalid_transaction_state';
    END IF;
    RETURN QUERY
    WITH l AS (
        SELECT h.id FROM txn_headers h
         WHERE h.id = ANY(p_header_ids)
         ORDER BY h.id
           FOR NO KEY UPDATE
    )
    SELECT count(*)::integer FROM l;
END;
$$;

COMMENT ON FUNCTION fn_lock_txn_headers(uuid[]) IS
    'Locks the given headers in id order until the caller''s transaction ends, so '
    'recomputes of overlapping header sets cannot deadlock. Returns how many it locked. '
    'Refuses any isolation level but READ COMMITTED.';

GRANT EXECUTE ON FUNCTION fn_lock_txn_headers(uuid[]) TO coffer_app, coffer_service;

-- ---------------------------------------------------------------------------
-- A set-based writer reads which headers carry a tag, or have a leg on an account,
-- before it changes that tag or account, and recomputes those headers. A tag link or
-- a leg another request adds in between would be changed with them and never
-- recomputed. Locking the tag or account row FOR UPDATE before that read closes the
-- window: a new link, leg or owner reference takes a KEY SHARE lock on the row it
-- references, which FOR UPDATE excludes, so it waits for the writer's commit; one
-- already in flight makes the writer wait for its commit, and the writer then reads it.
-- (FOR NO KEY UPDATE, which an UPDATE of the row's other columns takes, does not exclude
-- KEY SHARE, so the writer's own UPDATE of the row is not enough.) In id order, like the
-- header lock, and READ COMMITTED only, for the same reason.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_lock_tags(p_tag_ids uuid[])
RETURNS TABLE (locked integer)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION
            'fn_lock_tags must run in a READ COMMITTED transaction, not %: the writer reads '
            'the tag''s links after the lock, which only READ COMMITTED sees as committed',
            current_setting('transaction_isolation')
            USING ERRCODE = 'invalid_transaction_state';
    END IF;
    RETURN QUERY
    WITH l AS (
        SELECT t.id FROM tags t
         WHERE t.id = ANY(p_tag_ids)
         ORDER BY t.id
           FOR UPDATE
    )
    SELECT count(*)::integer FROM l;
END;
$$;

COMMENT ON FUNCTION fn_lock_tags(uuid[]) IS
    'Locks the given tags FOR UPDATE in id order until the caller''s transaction ends, so no '
    'link to them is added until it does. Returns how many it locked. Refuses any isolation '
    'level but READ COMMITTED.';

GRANT EXECUTE ON FUNCTION fn_lock_tags(uuid[]) TO coffer_app, coffer_service;

CREATE OR REPLACE FUNCTION fn_lock_accounts(p_account_ids uuid[])
RETURNS TABLE (locked integer)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION
            'fn_lock_accounts must run in a READ COMMITTED transaction, not %: the writer reads '
            'the account''s legs after the lock, which only READ COMMITTED sees as committed',
            current_setting('transaction_isolation')
            USING ERRCODE = 'invalid_transaction_state';
    END IF;
    RETURN QUERY
    WITH l AS (
        SELECT a.id FROM accounts a
         WHERE a.id = ANY(p_account_ids)
         ORDER BY a.id
           FOR UPDATE
    )
    SELECT count(*)::integer FROM l;
END;
$$;

COMMENT ON FUNCTION fn_lock_accounts(uuid[]) IS
    'Locks the given accounts FOR UPDATE in id order until the caller''s transaction ends, so '
    'no leg or transaction referencing them is added until it does. Returns how many it '
    'locked. Refuses any isolation level but READ COMMITTED.';

GRANT EXECUTE ON FUNCTION fn_lock_accounts(uuid[]) TO coffer_app, coffer_service;

-- ---------------------------------------------------------------------------
-- resolved_transactions exposes the stored presentation of the row's leg (l) and of
-- the posting's other leg (other), from the two txn_legs rows the view already
-- joins: a reader that presents a line from the other leg's account reads it with
-- no join of its own.
--
-- CREATE OR REPLACE with 230's column list, types and order unchanged and the new
-- columns appended, so register_filtered_entries (RETURNS SETOF resolved_transactions)
-- and register_entry_keys keep working without a DROP ... CASCADE.
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

-- security_invoker is the RLS boundary, and CREATE OR REPLACE VIEW does NOT
-- preserve reloptions (see 228, 230). Re-assert it, always.
ALTER VIEW resolved_transactions SET (security_invoker = true);

-- ---------------------------------------------------------------------------
-- Snapshots: txn_leg_note_tags is captured, inserted after txn_legs and tags,
-- cleared with the ledger's other rows. Each body below is the latest definition
-- (258, 262) with that one table added; nothing else in them changes.
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_part_names()
 RETURNS text[]
 LANGUAGE sql
 IMMUTABLE
AS $function$
    SELECT ARRAY[
        'accounts', 'securities', 'security_components', 'account_folders',
        'account_external_ids', 'security_prices', 'security_splits', 'holdings',
        'account_folder_members', 'txn_headers', 'txn_legs', 'txn_leg_recon',
        'lots', 'realized_gains', 'txn_header_originals',
        'tags', 'txn_header_tags', 'txn_leg_tags', 'txn_leg_note_tags', 'provider_security_mappings',
        'recurring_transactions', 'recurring_occurrence_exceptions', 'loan_terms',
        'budget_targets', 'saved_reports', 'report_pins', 'ledger_operation_promotions'
    ];
$function$;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_insert_order()
 RETURNS text[]
 LANGUAGE sql
 IMMUTABLE
AS $function$
    SELECT ARRAY[
        'accounts', 'loan_terms', 'budget_targets', 'securities',
        'security_components', 'tags',
        'account_external_ids', 'security_prices', 'security_splits', 'holdings',
        'account_folders', 'account_folder_members',
        'provider_security_mappings', 'recurring_transactions',
        'txn_headers', 'txn_legs', 'lots', 'txn_leg_recon', 'realized_gains',
        'recurring_occurrence_exceptions',
        'txn_header_originals', 'txn_header_tags', 'txn_leg_tags', 'txn_leg_note_tags',
        'saved_reports', 'report_pins', 'ledger_operation_promotions'
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
        'txn_leg_note_tags',                (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_note_tags t WHERE t.ledger_id = p_ledger_id),
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

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_restore(p_ledger_id uuid, p_payload text)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE
    v_payload  jsonb := p_payload::jsonb;
    v_expected jsonb;
BEGIN
    PERFORM fn_snapshot_require_consistent_read('fn_ledger_snapshot_restore');

    SELECT COALESCE(jsonb_object_agg(t, jsonb_array_length(COALESCE(v_payload -> t, '[]'::jsonb))), '{}'::jsonb)
      INTO v_expected
      FROM unnest(fn_ledger_snapshot_part_names()) AS t;

    PERFORM fn_ledger_snapshot_clear(p_ledger_id);

    -- ----- Insert rows from the payload (forward-FK order) --------------
    INSERT INTO accounts                   SELECT * FROM jsonb_populate_recordset(NULL::accounts,                   v_payload->'accounts');
    INSERT INTO loan_terms                 SELECT * FROM jsonb_populate_recordset(NULL::loan_terms,                 v_payload->'loan_terms');
    INSERT INTO budget_targets             SELECT * FROM jsonb_populate_recordset(NULL::budget_targets,             v_payload->'budget_targets');
    INSERT INTO securities                 SELECT * FROM jsonb_populate_recordset(NULL::securities,                 v_payload->'securities');
    INSERT INTO security_components        SELECT * FROM jsonb_populate_recordset(NULL::security_components,        v_payload->'security_components');
    INSERT INTO tags                       SELECT * FROM jsonb_populate_recordset(NULL::tags,                       v_payload->'tags');
    INSERT INTO account_external_ids       SELECT * FROM jsonb_populate_recordset(NULL::account_external_ids,       v_payload->'account_external_ids');
    INSERT INTO security_prices            SELECT * FROM jsonb_populate_recordset(NULL::security_prices,            v_payload->'security_prices');
    INSERT INTO security_splits            SELECT * FROM jsonb_populate_recordset(NULL::security_splits,            v_payload->'security_splits');
    INSERT INTO holdings                   SELECT * FROM jsonb_populate_recordset(NULL::holdings,                   v_payload->'holdings');
    INSERT INTO account_folders            SELECT * FROM jsonb_populate_recordset(NULL::account_folders,            v_payload->'account_folders');
    INSERT INTO account_folder_members     SELECT * FROM jsonb_populate_recordset(NULL::account_folder_members,     v_payload->'account_folder_members');
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
    INSERT INTO txn_leg_tags               SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_tags,               v_payload->'txn_leg_tags');
    INSERT INTO txn_leg_note_tags          SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_note_tags,          v_payload->'txn_leg_note_tags');
    INSERT INTO saved_reports              SELECT * FROM jsonb_populate_recordset(NULL::saved_reports,              v_payload->'saved_reports');
    INSERT INTO report_pins                SELECT * FROM jsonb_populate_recordset(NULL::report_pins,                v_payload->'report_pins');
    INSERT INTO ledger_operation_promotions SELECT * FROM jsonb_populate_recordset(NULL::ledger_operation_promotions, v_payload->'ledger_operation_promotions');

    PERFORM fn_ledger_snapshot_reconcile_external_refs(p_ledger_id);
    -- Fire the deferred checks here, where a violation names this function,
    -- rather than at the caller's COMMIT.
    SET CONSTRAINTS ALL IMMEDIATE;
    PERFORM fn_ledger_snapshot_verify_restored(p_ledger_id, v_expected);

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
    DELETE FROM report_pins                WHERE ledger_id = p_ledger_id;
    DELETE FROM saved_reports              WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_tags               WHERE ledger_id = p_ledger_id;
    -- Deleted in one statement rather than row by row through the cascades from
    -- its legs and tags.
    DELETE FROM txn_leg_note_tags          WHERE ledger_id = p_ledger_id;
    -- References txn_headers; captured since mig 247. Its parent run in
    -- ledger_operations is NOT cleared — the run log survives a rollback.
    DELETE FROM ledger_operation_promotions WHERE ledger_id = p_ledger_id;
    -- Transaction graph.
    DELETE FROM txn_legs                   WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_headers                WHERE ledger_id = p_ledger_id;
    -- Holdings / folders / per-security data. Folders are deleted explicitly:
    -- deleting accounts cascades only the members, and a folder left behind would
    -- sit beside its restored copy.
    DELETE FROM account_folder_members     WHERE ledger_id = p_ledger_id;
    DELETE FROM account_folders            WHERE ledger_id = p_ledger_id;
    DELETE FROM holdings                   WHERE ledger_id = p_ledger_id;
    DELETE FROM security_prices            WHERE ledger_id = p_ledger_id;
    DELETE FROM security_components        WHERE ledger_id = p_ledger_id;
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
    DELETE FROM report_pins                  WHERE ledger_id = p_ledger_id;
    DELETE FROM saved_reports                WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_tags                 WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_leg_note_tags            WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_legs                     WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_headers                  WHERE ledger_id = p_ledger_id;
    DELETE FROM account_folder_members       WHERE ledger_id = p_ledger_id;
    DELETE FROM account_folders              WHERE ledger_id = p_ledger_id;
    DELETE FROM holdings                     WHERE ledger_id = p_ledger_id;
    DELETE FROM security_prices              WHERE ledger_id = p_ledger_id;
    DELETE FROM security_components          WHERE ledger_id = p_ledger_id;
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
-- The snapshot writer, chunked on every table's primary key.
--
-- Each chunk is the next p_chunk_rows rows of the ledger in primary-key order: the
-- first from the start, each later one strictly after the last key the previous
-- chunk wrote, compared as a row value so a composite key resumes exactly where it
-- stopped and every chunk but the last holds exactly p_chunk_rows rows. The key's
-- columns and their types come from the catalog, so a table added to the capture
-- later needs nothing here; one with no primary key is refused rather than written
-- in one piece. The last key is read back from the chunk's last row (each column by
-- name, cast to its own type).
--
-- The count check of 247 stays: what the chunks hold must equal what the ledger
-- has, in the caller's REPEATABLE READ snapshot.
-- ---------------------------------------------------------------------------
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
    v_keys    text;   -- t.a AS k1, t.b AS k2
    v_order   text;   -- t.a, t.b
    v_agg     text;   -- k1, k2
    v_after   text;   -- (t.a, t.b) > (($2 ->> 'a')::uuid, ($2 ->> 'b')::uuid)
    v_count   bigint;
    v_written bigint  := 0;
    v_last    jsonb;
    v_seq     integer := 0;
    v_rows    integer;
    v_chunk   jsonb;
    v_bytes   bigint  := 0;
BEGIN
    SELECT string_agg(format('t.%I AS k%s', a.attname, k.ord), ', ' ORDER BY k.ord),
           string_agg(format('t.%I', a.attname), ', ' ORDER BY k.ord),
           string_agg(format('k%s', k.ord), ', ' ORDER BY k.ord),
           format('(%s) > (%s)',
                  string_agg(format('t.%I', a.attname), ', ' ORDER BY k.ord),
                  string_agg(format('($2 ->> %L)::%s', a.attname, format_type(a.atttypid, a.atttypmod)),
                             ', ' ORDER BY k.ord))
      INTO v_keys, v_order, v_agg, v_after
      FROM pg_index i
     CROSS JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
      JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
     WHERE i.indrelid = p_table::regclass
       AND i.indisprimary;

    IF v_order IS NULL THEN
        RAISE EXCEPTION
            'snapshot part %: the table has no primary key to chunk its capture on; '
            'give it one before capturing it', p_table;
    END IF;

    EXECUTE format('SELECT count(*) FROM %I t WHERE t.ledger_id = $1', p_table)
      INTO v_count USING p_ledger_id;

    LOOP
        EXECUTE format(
            'WITH c AS (
                 SELECT %s, to_jsonb(t) AS j
                   FROM %I t
                  WHERE t.ledger_id = $1 %s
                  ORDER BY %s
                  LIMIT $3
             )
             SELECT COALESCE(jsonb_agg(j ORDER BY %s), ''[]''::jsonb),
                    count(*)::integer
               FROM c',
            v_keys, p_table, CASE WHEN v_seq = 0 THEN '' ELSE 'AND ' || v_after END, v_order, v_agg)
        INTO v_chunk, v_rows
        USING p_ledger_id, v_last, p_chunk_rows;

        EXIT WHEN v_rows = 0 AND v_seq > 0;

        -- seq=0 is written even when empty: fn_ledger_snapshot_restore (mig 188)
        -- distinguishes an absent payload key from an empty '[]'.
        INSERT INTO ledger_snapshot_parts (snapshot_id, part_name, seq, content)
        VALUES (p_snapshot_id, p_table, v_seq, v_chunk);

        v_written := v_written + v_rows;
        v_bytes   := v_bytes + octet_length(v_chunk::text);
        v_seq     := v_seq + 1;

        EXIT WHEN v_rows < p_chunk_rows;

        v_last := v_chunk -> -1;
    END LOOP;

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

-- ===========================================================================
-- THE DATA STEPS
--
-- Existing memos and tags move to where the per-side rule reads them; the C# step
-- after this script then stores what the rule shows for every leg. In order:
--
--   Step 0  blank memos become NULL (blank always means inherit).
--   Step A  Moneydance bank-route rows and reminder templates still as an old
--           importer wrote them are rewritten to what the importer writes now,
--           from the payload each row keeps.
--   Step B  Moneydance investment-route rows likewise.
--   Step M  tags whose names share a key are merged into the earliest created, saved
--           reports filtered on a merged-away tag follow it, and the unique index on
--           the key is created.
--   Step C  every remaining one-posting header that still holds a memo or tags: the
--           values move onto its two legs so each register keeps what it shows, and
--           the header is cleared.
--   Step D  multi-posting investment events with values on legs of their investment
--           side: the values join the event's header and the legs are cleared.
--   End     the invariants, over every header; a violation fails the upgrade.
--
-- WHAT A ROW SHOWED IS KEPT. Before this release a register showed a leg's own memo,
-- else the header's (resolved_transactions, 230), and on a one-posting row the
-- header's tags. Every step below writes values that make each register show the
-- same, except where a decision accepts a change (ADR-0108 D16), each counted
-- or named where it happens.
--
-- NEVER OVERWRITES THE USER. Steps A and B rewrite a field only while it is exactly
-- what one importer vintage wrote for the row's payload; a row whose shape, owner or
-- action differs from what the importer left was edited, and is left to Step C (a
-- one-posting row) or as it is. The vintages are recognised by state, never by when
-- a row was written:
--
--   V1  header tags = the transaction's tags and every split's, no leg tags; names
--       trimmed only.
--   V2  header tags = the transaction's tags, each line's own on its home leg;
--       names trimmed only.
--   V3  as V2, names trimmed, capped at 64 UTF-16 units and trimmed again.
--
-- The memo placement is the same in every vintage: the memo (else the feed's) on the
-- header, and on a split each line's text on both of its legs. No vintage imported
-- a template's tags or an investment row's tags.
--
-- DETERMINISTIC. Every value is read off stored rows. A tag this step creates has
-- the id md5(ledger_id || '/' || name) and the created_at of the earliest header
-- that uses it; a tag link it creates takes its header's created_at, and a link it
-- moves keeps its own. No clock is read anywhere.
--
-- RERUNNABLE. A row already rewritten no longer looks like what an old importer
-- wrote (or looks like it and is rewritten to the same values); a header already
-- cleared is not a candidate again. A second run over the result changes nothing.
--
-- PLANNER STATISTICS. Each step reads what the one before wrote inside this one
-- transaction, where the statistics still describe the table before it. Each step
-- therefore starts with ANALYZE of what it reads, and of its own work tables.
-- ===========================================================================

-- ---------------------------------------------------------------------------
-- Helpers, dropped at the end.
-- ---------------------------------------------------------------------------

-- Moneydance writes every field as a JSON string, and the importer reads a field only
-- when it is one (as in 260 and 263).
CREATE OR REPLACE FUNCTION mig264_md_text(payload jsonb, key text) RETURNS text
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT CASE WHEN jsonb_typeof(payload -> key) = 'string' THEN payload ->> key END;
$fn$;

-- .NET's String.Trim(): the char.IsWhiteSpace set, spelled as escapes, because
-- [[:space:]] follows the server's locale (the class of 260 and tag_name_key).
CREATE OR REPLACE FUNCTION mig264_trim(value text) RETURNS text
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT regexp_replace(value,
        '^[\u0009-\u000D \u0085   -     　]+'
        || '|[\u0009-\u000D \u0085   -     　]+$',
        '', 'g');
$fn$;

-- .NET's TrimEnd(), the same set.
CREATE OR REPLACE FUNCTION mig264_trim_end(value text) RETURNS text
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT regexp_replace(value,
        '[\u0009-\u000D \u0085   -     　]+$', '');
$fn$;

-- Empty or only white space (string.IsNullOrWhiteSpace for a non-null value).
CREATE OR REPLACE FUNCTION mig264_is_blank(value text) RETURNS boolean
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT value ~ '^[\u0009-\u000D \u0085   -     　]*$';
$fn$;

-- The old importers' NullIfEmpty: blank to NULL, anything else verbatim (no trim).
CREATE OR REPLACE FUNCTION mig264_blank_to_null(value text) RETURNS text
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT CASE WHEN value IS NULL OR mig264_is_blank(value) THEN NULL ELSE value END;
$fn$;

-- AnnotationText.NormalizeMemo: trimmed, and blank is no memo.
CREATE OR REPLACE FUNCTION mig264_norm(value text) RETURNS text
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT NULLIF(mig264_trim(value), '');
$fn$;

-- AnnotationText.JoinMemos: each trimmed, a blank part dropped, equal parts kept once
-- (ordinal), different parts joined first-then-second.
CREATE OR REPLACE FUNCTION mig264_join(first text, second text) RETURNS text
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT CASE
             WHEN mig264_norm(first) IS NULL THEN mig264_norm(second)
             WHEN mig264_norm(second) IS NULL
               OR mig264_norm(first) = mig264_norm(second) COLLATE "C" THEN mig264_norm(first)
             ELSE mig264_norm(first) || ' · ' || mig264_norm(second)
           END;
$fn$;

-- The first n UTF-16 code units of a string, as .NET's s[..n] counts: a character
-- outside the Basic Multilingual Plane is two units. A cut that would split such a
-- character stops before it; .NET would leave half of it, which no stored name can
-- hold (PostgreSQL text cannot), so the shorter form only ever fails to match.
CREATE OR REPLACE FUNCTION mig264_utf16_left(value text, n integer) RETURNS text
LANGUAGE plpgsql IMMUTABLE AS $fn$
DECLARE
    units  integer := 0;
    result text := '';
    ch     text;
    width  integer;
BEGIN
    FOREACH ch IN ARRAY regexp_split_to_array(value, '') LOOP
        width := CASE WHEN ascii(ch) > 65535 THEN 2 ELSE 1 END;
        EXIT WHEN units + width > n;
        units := units + width;
        result := result || ch;
    END LOOP;
    RETURN result;
END;
$fn$;

-- The UTF-16 length .NET's string.Length reports.
CREATE OR REPLACE FUNCTION mig264_utf16_length(value text) RETURNS integer
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT COALESCE(sum(CASE WHEN ascii(c) > 65535 THEN 2 ELSE 1 END), 0)::integer
      FROM regexp_split_to_table(value, '') AS c;
$fn$;

-- A comma-separated Moneydance tag list as names, each once, in order. capped: the
-- importer's TagName.Normalize since the 64-unit cap (V3, and the importer now):
-- trimmed, cut to 64 units, trimmed at the end again. Not capped: V1 and V2, trimmed
-- only.
CREATE OR REPLACE FUNCTION mig264_tag_names(raw text, capped boolean) RETURNS text[]
LANGUAGE plpgsql IMMUTABLE AS $fn$
DECLARE
    piece  text;
    nm     text;
    result text[] := '{}';
BEGIN
    IF raw IS NULL OR mig264_is_blank(raw) THEN
        RETURN result;
    END IF;
    FOREACH piece IN ARRAY string_to_array(raw, ',') LOOP
        nm := mig264_trim(piece);
        IF capped AND mig264_utf16_length(nm) > 64 THEN
            nm := mig264_trim_end(mig264_utf16_left(nm, 64));
        END IF;
        IF nm <> '' AND NOT (nm COLLATE "C" = ANY (result)) THEN
            result := result || nm;
        END IF;
    END LOOP;
    RETURN result;
END;
$fn$;

-- MdTxn.ExtractSplits: <prefix>N.acctid from 0, at most 32, stopping at the first gap.
CREATE OR REPLACE FUNCTION mig264_md_splits(payload jsonb, prefix text)
RETURNS TABLE (idx integer, acctid text, descr text, tags text, splittype text)
LANGUAGE plpgsql IMMUTABLE AS $fn$
BEGIN
    FOR i IN 0..31 LOOP
        acctid := mig264_md_text(payload, prefix || i || '.acctid');
        EXIT WHEN acctid IS NULL;
        idx := i;
        descr := mig264_md_text(payload, prefix || i || '.desc');
        tags := mig264_md_text(payload, prefix || i || '.tags');
        splittype := mig264_md_text(payload, prefix || i || '.invest.splittype');
        RETURN NEXT;
    END LOOP;
END;
$fn$;

-- MdTxn.IsInvestmentShape (as 260): a non-empty txntype, or an investment xfer_type.
CREATE OR REPLACE FUNCTION mig264_md_is_investment_shape(payload jsonb, prefix text) RETURNS boolean
LANGUAGE sql IMMUTABLE AS $fn$
    SELECT COALESCE(mig264_md_text(payload, prefix || 'invest.txntype'), '') <> ''
        OR COALESCE(mig264_md_text(payload, prefix || 'xfer_type'), '') IN
           ('xfrtp_buysell', 'xfrtp_buysellxfr', 'xfrtp_dividend',
            'xfrtp_dividendxfr', 'xfrtp_miscincexp');
$fn$;

-- AnnotationText.JoinDistinctMemos, several memos as one: each trimmed, a blank part
-- dropped, a part equal to one already kept dropped (ordinal, so case-sensitive; a
-- part is compared whole, never split at " · "), the rest joined with " · " in order.
-- For two parts it is mig264_join. Steps B and D join an event's memos with it.
CREATE OR REPLACE FUNCTION mig264_join_distinct(parts text[]) RETURNS text
LANGUAGE plpgsql IMMUTABLE AS $fn$
DECLARE
    kept text[] := '{}';
    part text;
    norm text;
BEGIN
    FOREACH part IN ARRAY COALESCE(parts, '{}'::text[]) LOOP
        norm := mig264_norm(part);
        IF norm IS NOT NULL AND NOT (norm COLLATE "C" = ANY (kept)) THEN
            kept := kept || norm;
        END IF;
    END LOOP;
    RETURN NULLIF(array_to_string(kept, ' · '), '');
END;
$fn$;

-- The tags every rewrite below targets: one row per (header, place, name), place
-- NULL for the header and a leg id for a leg. A step fills it for the headers whose
-- tags it rewrites (mig264_retag), and mig264_apply_tags then makes those headers'
-- links exactly this set.
CREATE TEMP TABLE mig264_retag (header_id uuid PRIMARY KEY) ON COMMIT DROP;
CREATE TEMP TABLE mig264_target_tag (
    header_id  uuid        NOT NULL,
    leg_id     uuid        NULL,
    ledger_id  uuid        NOT NULL,
    name       text        NOT NULL,
    ord        integer     NOT NULL,
    created_at timestamptz NOT NULL
) ON COMMIT DROP;
-- Work tables of mig264_apply_tags, emptied by each call.
CREATE TEMP TABLE mig264_target_key (
    header_id uuid NOT NULL, leg_id uuid NULL, ledger_id uuid NOT NULL, name text NOT NULL,
    key text NOT NULL, ord integer NOT NULL, created_at timestamptz NOT NULL
) ON COMMIT DROP;
CREATE TEMP TABLE mig264_key (
    ledger_id uuid NOT NULL, key text NOT NULL, tag_id uuid NOT NULL, PRIMARY KEY (ledger_id, key)
) ON COMMIT DROP;
CREATE TEMP TABLE mig264_resolved (
    header_id uuid NOT NULL, leg_id uuid NULL, ledger_id uuid NOT NULL, tag_id uuid NOT NULL,
    created_at timestamptz NOT NULL
) ON COMMIT DROP;
CREATE TEMP TABLE mig264_old_header_links (
    header_id uuid NOT NULL, tag_id uuid NOT NULL, created_at timestamptz NOT NULL,
    PRIMARY KEY (header_id, tag_id)
) ON COMMIT DROP;

-- Make the tag links of every header in mig264_retag exactly mig264_target_tag.
-- A name resolves to the ledger's tag with its key (tag_name_key), the earliest
-- created then the lowest id where several share it (only before Step M; the same
-- pick Step M keeps); a key no tag has is created, spelled as the earliest header
-- that uses it spells it. A link that stays keeps its row; a header link moved onto
-- a leg of its header keeps its created_at; any other new link takes its header's.
CREATE OR REPLACE FUNCTION mig264_apply_tags() RETURNS TABLE (tags_created bigint, links_written bigint)
LANGUAGE plpgsql AS $fn$
DECLARE
    v_created bigint;
    v_links   bigint := 0;
    v_n       bigint;
BEGIN
    -- Each name's key, computed once.
    INSERT INTO mig264_target_key (header_id, leg_id, ledger_id, name, key, ord, created_at)
    SELECT t.header_id, t.leg_id, t.ledger_id, t.name, public.tag_name_key(t.name), t.ord, t.created_at
      FROM mig264_target_tag t;
    ANALYZE mig264_retag;
    ANALYZE mig264_target_key;

    -- The tag each key of those ledgers resolves to: the earliest created, then the
    -- lowest id.
    INSERT INTO mig264_key (ledger_id, key, tag_id)
    SELECT DISTINCT ON (x.ledger_id, public.tag_name_key(x.name)) x.ledger_id, public.tag_name_key(x.name), x.id
      FROM public.tags x
     WHERE x.ledger_id IN (SELECT DISTINCT k.ledger_id FROM mig264_target_key k)
     ORDER BY x.ledger_id, public.tag_name_key(x.name), x.created_at, x.id;

    -- A key no tag has: created, spelled as the earliest header using it spells it,
    -- with that header's created_at.
    WITH wanted AS (
        SELECT DISTINCT ON (k.ledger_id, k.key) k.ledger_id, k.key, k.name, k.created_at
          FROM mig264_target_key k
         WHERE NOT EXISTS (SELECT 1 FROM mig264_key x WHERE x.ledger_id = k.ledger_id AND x.key = k.key)
         ORDER BY k.ledger_id, k.key, k.created_at, k.header_id, k.ord
    ), ins AS (
        INSERT INTO public.tags (id, ledger_id, name, created_at)
        SELECT md5(w.ledger_id::text || '/' || w.name)::uuid, w.ledger_id, w.name, w.created_at
          FROM wanted w
        RETURNING id, ledger_id, name
    )
    INSERT INTO mig264_key (ledger_id, key, tag_id)
    SELECT i.ledger_id, public.tag_name_key(i.name), i.id FROM ins i;
    GET DIAGNOSTICS v_created = ROW_COUNT;
    ANALYZE mig264_key;

    INSERT INTO mig264_resolved (header_id, leg_id, ledger_id, tag_id, created_at)
    SELECT DISTINCT k.header_id, k.leg_id, k.ledger_id, x.tag_id, k.created_at
      FROM mig264_target_key k
      JOIN mig264_key x ON x.ledger_id = k.ledger_id AND x.key = k.key;
    ANALYZE mig264_resolved;

    -- What each retagged header held before, for the links that move onto a leg.
    INSERT INTO mig264_old_header_links (header_id, tag_id, created_at)
    SELECT ht.header_id, ht.tag_id, ht.created_at
      FROM public.txn_header_tags ht
      JOIN mig264_retag r ON r.header_id = ht.header_id;
    ANALYZE mig264_old_header_links;

    DELETE FROM public.txn_header_tags ht
     USING mig264_retag r
     WHERE ht.header_id = r.header_id
       AND NOT EXISTS (SELECT 1 FROM mig264_resolved x
                        WHERE x.header_id = ht.header_id AND x.leg_id IS NULL AND x.tag_id = ht.tag_id);
    GET DIAGNOSTICS v_n = ROW_COUNT;
    v_links := v_links + v_n;

    DELETE FROM public.txn_leg_tags lt
     USING public.txn_legs l, mig264_retag r
     WHERE lt.leg_id = l.id AND l.header_id = r.header_id
       AND NOT EXISTS (SELECT 1 FROM mig264_resolved x
                        WHERE x.leg_id = lt.leg_id AND x.tag_id = lt.tag_id);
    GET DIAGNOSTICS v_n = ROW_COUNT;
    v_links := v_links + v_n;

    INSERT INTO public.txn_header_tags (header_id, tag_id, ledger_id, created_at)
    SELECT x.header_id, x.tag_id, x.ledger_id, x.created_at
      FROM mig264_resolved x
     WHERE x.leg_id IS NULL
    ON CONFLICT (header_id, tag_id) DO NOTHING;
    GET DIAGNOSTICS v_n = ROW_COUNT;
    v_links := v_links + v_n;

    INSERT INTO public.txn_leg_tags (leg_id, tag_id, ledger_id, created_at)
    SELECT x.leg_id, x.tag_id, x.ledger_id, COALESCE(o.created_at, x.created_at)
      FROM mig264_resolved x
      LEFT JOIN mig264_old_header_links o ON o.header_id = x.header_id AND o.tag_id = x.tag_id
     WHERE x.leg_id IS NOT NULL
    ON CONFLICT (leg_id, tag_id) DO NOTHING;
    GET DIAGNOSTICS v_n = ROW_COUNT;
    v_links := v_links + v_n;

    TRUNCATE mig264_retag, mig264_target_tag, mig264_target_key, mig264_key, mig264_resolved,
             mig264_old_header_links;

    tags_created := v_created;
    links_written := v_links;
    RETURN NEXT;
END;
$fn$;

-- ---------------------------------------------------------------------------
-- Step 0. Blank memos become NULL (ADR-0108 D13): empty or .NET white space only, on every
-- header and leg, templates and occurrences included. A blank leg memo used to hide
-- the header's memo on its row; that row now shows the value it inherits, a stated
-- display change. txn_header_originals is the feed's archive and is not touched.
-- ---------------------------------------------------------------------------
DO $step0$
DECLARE
    v_headers bigint;
    v_legs    bigint;
BEGIN
    UPDATE public.txn_headers SET memo = NULL WHERE memo IS NOT NULL AND mig264_is_blank(memo);
    GET DIAGNOSTICS v_headers = ROW_COUNT;
    UPDATE public.txn_legs SET leg_memo = NULL WHERE leg_memo IS NOT NULL AND mig264_is_blank(leg_memo);
    GET DIAGNOSTICS v_legs = ROW_COUNT;
    RAISE NOTICE '264 step 0: % header memo(s) and % leg memo(s) were blank and are now NULL', v_headers, v_legs;
END $step0$;

ANALYZE public.txn_headers;
ANALYZE public.txn_legs;
ANALYZE public.accounts;
ANALYZE public.tags;
ANALYZE public.txn_header_tags;
ANALYZE public.txn_leg_tags;
ANALYZE public.recurring_transactions;

-- ---------------------------------------------------------------------------
-- Step A. Moneydance bank-route rows and reminder templates.
--
-- A row is a candidate only while its originating account (263) is its resolved
-- Moneydance home, its legs are still the postings the importer emitted (one per
-- split on another account, each a home leg and one other), and its action is what
-- the importer and 260 left: 'transfer' for a one-posting row whose home is a
-- brokerage, NULL otherwise. A converted, split, re-homed or reshaped row was
-- edited.
--
-- Its memo is rewritten only while the header holds the memo as written and the legs
-- hold what that vintage put there (nothing on a one-split; each line's text on both
-- legs of a split); its tags only while they are one vintage's placement of the
-- payload's names (a template's: none). The targets are what the importer writes now
-- (MoneydanceNotes.PlanBankRoute):
--
--   one-split   the home leg: the memo as written and the split's tags; the other
--               leg: the split's text where it is the user's own (not blank, not the
--               payee, not the memo) and the transaction's tags; the header nothing.
--   brokerage   (a one-posting cash move, 'transfer') the brokerage leg: the memo
--               joined with that text, and every tag; the other leg nothing.
--   split       the header: the memo; each line's home leg: its text and its tags
--               with the transaction's; the other legs nothing, so they show their
--               line's value inherited.
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE mig264_md ON COMMIT DROP AS
SELECT h.id AS header_id, h.ledger_id, h.created_at, h.action, h.memo AS header_memo,
       h.originating_account_id AS home_id, a.account_type AS home_type,
       FALSE AS is_template, h.provider_raw_payload AS payload, ''::text AS prefix,
       mig264_md_text(h.provider_raw_payload, 'acctid') AS md_home,
       COALESCE(mig264_blank_to_null(mig264_md_text(h.provider_raw_payload, 'memo')),
                mig264_blank_to_null(mig264_md_text(h.provider_raw_payload, 'ol.orig-memo'))) AS written_old,
       COALESCE(mig264_norm(mig264_md_text(h.provider_raw_payload, 'memo')),
                mig264_norm(mig264_md_text(h.provider_raw_payload, 'ol.orig-memo'))) AS written_new,
       mig264_md_text(h.provider_raw_payload, 'desc') AS description,
       mig264_md_text(h.provider_raw_payload, 'tags') AS txn_tags,
       FALSE AS investment_shaped
  FROM public.txn_headers h
  JOIN public.accounts a ON a.id = h.originating_account_id
 WHERE h.import_source = 'moneydance_export'
   AND NOT h.is_recurring_template
   AND h.provider_raw_payload IS NOT NULL
   AND NOT mig264_md_is_investment_shape(h.provider_raw_payload, '')
   AND mig264_md_text(a.provider_raw_payload, 'id') = mig264_md_text(h.provider_raw_payload, 'acctid')
UNION ALL
-- A template: the reminder's JSON, its txn under "txn.*". The memo written is the
-- reminder's own, else its txn's; a reminder keeps no feed memo.
SELECT h.id, h.ledger_id, h.created_at, h.action, h.memo,
       h.originating_account_id, a.account_type,
       TRUE, rt.source_payload, 'txn.'::text,
       mig264_md_text(rt.source_payload, 'txn.acctid'),
       COALESCE(mig264_blank_to_null(mig264_md_text(rt.source_payload, 'memo')),
                mig264_blank_to_null(mig264_md_text(rt.source_payload, 'txn.memo'))),
       COALESCE(mig264_norm(mig264_md_text(rt.source_payload, 'memo')),
                mig264_norm(mig264_md_text(rt.source_payload, 'txn.memo'))),
       mig264_md_text(rt.source_payload, 'txn.desc'),
       mig264_md_text(rt.source_payload, 'txn.tags'),
       mig264_md_is_investment_shape(rt.source_payload, 'txn.')
  FROM public.txn_headers h
  JOIN public.recurring_transactions rt
    ON rt.template_header_id = h.id AND rt.ledger_id = h.ledger_id
  JOIN public.accounts a ON a.id = h.originating_account_id
 WHERE h.import_source = 'moneydance_export'
   AND h.is_recurring_template
   AND rt.origin = 'moneydance_import'
   AND rt.source_payload IS NOT NULL
   AND h.external_id = 'mdreminder:' || rt.external_id
   AND mig264_md_text(a.provider_raw_payload, 'id') = mig264_md_text(rt.source_payload, 'txn.acctid');

ALTER TABLE mig264_md ADD PRIMARY KEY (header_id);
ANALYZE mig264_md;

-- Every split of the payload; the importer emitted those on another account.
CREATE TEMP TABLE mig264_md_split ON COMMIT DROP AS
SELECT m.header_id, s.idx, s.acctid, s.descr, s.tags,
       s.acctid IS DISTINCT FROM m.md_home AS emitted
  FROM mig264_md m
 CROSS JOIN LATERAL mig264_md_splits(m.payload, m.prefix) s;
ALTER TABLE mig264_md_split ADD PRIMARY KEY (header_id, idx);
ANALYZE mig264_md_split;

CREATE TEMP TABLE mig264_md_leg ON COMMIT DROP AS
SELECT l.header_id, l.id AS leg_id, l.posting_index, l.account_id = m.home_id AS is_home, l.leg_memo
  FROM mig264_md m
  JOIN public.txn_legs l ON l.header_id = m.header_id;
ALTER TABLE mig264_md_leg ADD PRIMARY KEY (leg_id);
CREATE INDEX ON mig264_md_leg (header_id, posting_index);
ANALYZE mig264_md_leg;

-- The shape check, each fact aggregated once per header and joined by key.
CREATE TEMP TABLE mig264_md_row ON COMMIT DROP AS
WITH per_posting AS (
    SELECT g.header_id, g.posting_index, count(*) AS legs, count(*) FILTER (WHERE g.is_home) AS homes
      FROM mig264_md_leg g
     GROUP BY g.header_id, g.posting_index
), leg_shape AS (
    SELECT p.header_id, count(*) AS postings, bool_and(p.legs = 2 AND p.homes = 1) AS paired,
           array_agg(p.posting_index ORDER BY p.posting_index) AS indexes
      FROM per_posting p
     GROUP BY p.header_id
), split_shape AS (
    SELECT s.header_id, array_agg(s.idx ORDER BY s.idx) AS indexes
      FROM mig264_md_split s
     WHERE s.emitted
     GROUP BY s.header_id
)
SELECT m.header_id, m.ledger_id, m.created_at, m.is_template, m.written_old, m.written_new,
       m.description, m.txn_tags, m.header_memo, ls.postings,
       CASE WHEN m.is_template AND m.investment_shaped THEN NULL
            WHEN m.home_type = 'investment' AND ls.postings = 1 THEN 'transfer'
       END AS expected_action,
       m.action
  FROM mig264_md m
  JOIN leg_shape ls ON ls.header_id = m.header_id
  JOIN split_shape ss ON ss.header_id = m.header_id
 WHERE ls.paired AND ls.indexes = ss.indexes;

DELETE FROM mig264_md_row r WHERE r.action IS DISTINCT FROM r.expected_action;
ALTER TABLE mig264_md_row ADD PRIMARY KEY (header_id);
ALTER TABLE mig264_md_row ADD COLUMN kind text;
UPDATE mig264_md_row SET kind = CASE WHEN expected_action = 'transfer' THEN 'event'
                                     WHEN postings > 1 THEN 'split'
                                     ELSE 'one' END;
ANALYZE mig264_md_row;

-- Each leg with its split, and what the importer writes on it now.
CREATE TEMP TABLE mig264_md_target ON COMMIT DROP AS
SELECT g.header_id, g.leg_id, g.is_home, g.leg_memo, r.kind,
       s.descr AS split_descr, s.tags AS split_tags,
       -- The other side's own memo: the split's text where it is the user's.
       CASE WHEN mig264_norm(s.descr) IS NULL
              OR mig264_norm(s.descr) = mig264_norm(r.description) COLLATE "C"
              OR mig264_norm(s.descr) = r.written_new COLLATE "C" THEN NULL
            ELSE mig264_norm(s.descr) END AS counterpart_memo
  FROM mig264_md_leg g
  JOIN mig264_md_row r ON r.header_id = g.header_id
  JOIN mig264_md_split s ON s.header_id = g.header_id AND s.idx = g.posting_index;
ALTER TABLE mig264_md_target ADD PRIMARY KEY (leg_id);
ANALYZE mig264_md_target;

-- The memo check: the header holds the memo as written, and the legs what the
-- vintage put there.
CREATE TEMP TABLE mig264_md_memo_ok ON COMMIT DROP AS
SELECT r.header_id
  FROM mig264_md_row r
  JOIN (SELECT t.header_id,
               bool_and(CASE WHEN t.kind = 'split'
                             THEN t.leg_memo IS NOT DISTINCT FROM mig264_blank_to_null(t.split_descr)
                             ELSE t.leg_memo IS NULL END) AS legs_ok
          FROM mig264_md_target t
         GROUP BY t.header_id) lg ON lg.header_id = r.header_id
 WHERE r.header_memo IS NOT DISTINCT FROM r.written_old
   AND lg.legs_ok;
ALTER TABLE mig264_md_memo_ok ADD PRIMARY KEY (header_id);
ANALYZE mig264_md_memo_ok;

-- The tag check: the stored placement, by name, against each vintage's. A placement
-- is one string per header: every (place, name), sorted, joined with control
-- characters no name holds; place H is the header, "Ln h" and "Ln c" posting n's
-- home and other leg.
CREATE TEMP TABLE mig264_md_tags_ok ON COMMIT DROP AS
WITH stored AS (
    SELECT ht.header_id, ('H' || chr(30) || t.name) COLLATE "C" AS entry
      FROM public.txn_header_tags ht
      JOIN mig264_md_row r ON r.header_id = ht.header_id
      JOIN public.tags t ON t.id = ht.tag_id
    UNION
    SELECT g.header_id, ('L' || g.posting_index || CASE WHEN g.is_home THEN 'h' ELSE 'c' END || chr(30) || t.name) COLLATE "C"
      FROM public.txn_leg_tags lt
      JOIN mig264_md_leg g ON g.leg_id = lt.leg_id
      JOIN mig264_md_row r ON r.header_id = g.header_id
      JOIN public.tags t ON t.id = lt.tag_id
), stored_fp AS (
    SELECT s.header_id, string_agg(s.entry, chr(31) ORDER BY s.entry) AS fp
      FROM stored s
     GROUP BY s.header_id
), vintage AS (
    -- V1: every name on the header, trimmed only.
    SELECT r.header_id, 'v1'::text AS v, ('H' || chr(30) || n) COLLATE "C" AS entry
      FROM mig264_md_row r
     CROSS JOIN LATERAL unnest(mig264_tag_names(r.txn_tags, FALSE)) n
     WHERE NOT r.is_template
    UNION
    SELECT r.header_id, 'v1', ('H' || chr(30) || n) COLLATE "C"
      FROM mig264_md_row r
      JOIN mig264_md_split s ON s.header_id = r.header_id
     CROSS JOIN LATERAL unnest(mig264_tag_names(s.tags, FALSE)) n
     WHERE NOT r.is_template
    UNION
    -- V2 and V3: the transaction's on the header, each line's on its home leg.
    SELECT r.header_id, v.v, ('H' || chr(30) || n) COLLATE "C"
      FROM mig264_md_row r
     CROSS JOIN (VALUES ('v2', FALSE), ('v3', TRUE)) v(v, capped)
     CROSS JOIN LATERAL unnest(mig264_tag_names(r.txn_tags, v.capped)) n
     WHERE NOT r.is_template
    UNION
    SELECT r.header_id, v.v, ('L' || s.idx || 'h' || chr(30) || n) COLLATE "C"
      FROM mig264_md_row r
      JOIN mig264_md_split s ON s.header_id = r.header_id AND s.emitted
     CROSS JOIN (VALUES ('v2', FALSE), ('v3', TRUE)) v(v, capped)
     CROSS JOIN LATERAL unnest(mig264_tag_names(s.tags, v.capped)) n
     WHERE NOT r.is_template
), vintage_fp AS (
    SELECT x.header_id, x.v, string_agg(x.entry, chr(31) ORDER BY x.entry) AS fp
      FROM vintage x
     GROUP BY x.header_id, x.v
), states AS (
    -- Every vintage of every row, an empty placement included; a template's only
    -- state is no tags at all.
    SELECT r.header_id,
           CASE WHEN r.is_template THEN '' ELSE COALESCE(f.fp, '') END AS fp
      FROM mig264_md_row r
     CROSS JOIN (VALUES ('v1'), ('v2'), ('v3')) v(v)
      LEFT JOIN vintage_fp f ON f.header_id = r.header_id AND f.v = v.v
)
SELECT DISTINCT st.header_id
  FROM states st
  LEFT JOIN stored_fp sf ON sf.header_id = st.header_id
 WHERE COALESCE(sf.fp, '') COLLATE "C" = st.fp COLLATE "C";
ALTER TABLE mig264_md_tags_ok ADD PRIMARY KEY (header_id);
ANALYZE mig264_md_tags_ok;

DO $stepA$
DECLARE
    v_candidates bigint;
    v_rows       bigint;
    v_memo       bigint;
    v_tags       bigint;
    v_applied    record;
BEGIN
    SELECT count(*) INTO v_candidates FROM mig264_md;
    SELECT count(*) INTO v_rows FROM mig264_md_row;
    SELECT count(*) INTO v_memo FROM mig264_md_memo_ok;
    SELECT count(*) INTO v_tags FROM mig264_md_tags_ok;

    -- Memos.
    UPDATE public.txn_headers h
       SET memo = CASE WHEN r.kind = 'split' THEN r.written_new END
      FROM mig264_md_row r
      JOIN mig264_md_memo_ok ok ON ok.header_id = r.header_id
     WHERE h.id = r.header_id
       AND h.memo IS DISTINCT FROM CASE WHEN r.kind = 'split' THEN r.written_new END;

    UPDATE public.txn_legs l
       SET leg_memo = x.memo
      FROM (SELECT t.leg_id,
                   CASE
                     WHEN t.kind = 'split' THEN CASE WHEN t.is_home THEN mig264_norm(t.split_descr) END
                     WHEN t.kind = 'event' THEN CASE WHEN t.is_home THEN mig264_join(r.written_new, t.counterpart_memo) END
                     ELSE CASE WHEN t.is_home THEN r.written_new ELSE t.counterpart_memo END
                   END AS memo
              FROM mig264_md_target t
              JOIN mig264_md_row r ON r.header_id = t.header_id
              JOIN mig264_md_memo_ok ok ON ok.header_id = t.header_id) x
     WHERE l.id = x.leg_id
       AND l.leg_memo IS DISTINCT FROM x.memo;

    -- Tags.
    INSERT INTO mig264_retag (header_id) SELECT header_id FROM mig264_md_tags_ok;
    INSERT INTO mig264_target_tag (header_id, leg_id, ledger_id, name, ord, created_at)
    SELECT t.header_id, t.leg_id, r.ledger_id, n.name, n.ord, r.created_at
      FROM mig264_md_target t
      JOIN mig264_md_row r ON r.header_id = t.header_id
      JOIN mig264_md_tags_ok ok ON ok.header_id = t.header_id
     CROSS JOIN LATERAL unnest(
            CASE
              WHEN t.kind IN ('split', 'event') AND t.is_home
                   THEN mig264_tag_names(t.split_tags, TRUE) || mig264_tag_names(r.txn_tags, TRUE)
              WHEN t.kind = 'one' AND t.is_home THEN mig264_tag_names(t.split_tags, TRUE)
              WHEN t.kind = 'one' THEN mig264_tag_names(r.txn_tags, TRUE)
              ELSE '{}'::text[]
            END) WITH ORDINALITY AS n(name, ord);
    SELECT * INTO v_applied FROM mig264_apply_tags();

    RAISE NOTICE '264 step A: % Moneydance row(s) and template(s) on their home, % still as imported; '
                 '% memo(s) and % tag set(s) rewritten, the rest edited and left to step C; % tag(s) created, % link(s) written',
                 v_candidates, v_rows, v_memo, v_tags, v_applied.tags_created, v_applied.links_written;
END $stepA$;

-- ---------------------------------------------------------------------------
-- Step B. Moneydance investment-route rows.
--
-- A candidate's originating account is its resolved home brokerage and it carries
-- an action (an event moved to another brokerage was edited). The importer kept the
-- memo as written on the header and nothing on the legs, and imported no tags. Now
-- (MoneydanceNotes.InvestmentEvent) the event's one memo is the memo as written
-- then, in split order, each non-security split's text that is the user's own (not
-- blank, not the payee), each distinct memo once (mig264_join_distinct, the rule of
-- Step D), and its one tag set is every name the transaction or a split carries;
-- both sit on the brokerage leg of a one-posting event and on the header of a
-- multi-posting one. The memo is rewritten only while
-- the header and legs are as imported; the tags only while the event has none (an
-- event the user tagged is left as it is).
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE mig264_inv ON COMMIT DROP AS
WITH candidates AS (
    SELECT h.id AS header_id, h.ledger_id, h.created_at, h.memo AS header_memo,
           h.originating_account_id AS home_id, h.provider_raw_payload AS payload,
           COALESCE(mig264_blank_to_null(mig264_md_text(h.provider_raw_payload, 'memo')),
                    mig264_blank_to_null(mig264_md_text(h.provider_raw_payload, 'ol.orig-memo'))) AS written_old,
           COALESCE(mig264_norm(mig264_md_text(h.provider_raw_payload, 'memo')),
                    mig264_norm(mig264_md_text(h.provider_raw_payload, 'ol.orig-memo'))) AS written_new,
           mig264_norm(mig264_md_text(h.provider_raw_payload, 'desc')) AS payee,
           mig264_md_text(h.provider_raw_payload, 'tags') AS txn_tags
      FROM public.txn_headers h
      JOIN public.accounts a ON a.id = h.originating_account_id
     WHERE h.import_source = 'moneydance_export'
       AND NOT h.is_recurring_template
       AND h.provider_raw_payload IS NOT NULL
       AND h.action IS NOT NULL
       AND mig264_md_is_investment_shape(h.provider_raw_payload, '')
       AND a.holdings_account_id IS NOT NULL
       AND mig264_md_text(a.provider_raw_payload, 'id') = mig264_md_text(h.provider_raw_payload, 'acctid')
), legs AS (
    SELECT l.header_id, count(DISTINCT l.posting_index) AS postings,
           count(*) FILTER (WHERE l.leg_memo IS NOT NULL) AS memos,
           count(*) FILTER (WHERE l.account_id = c.home_id) AS home_legs,
           (min(l.id::text) FILTER (WHERE l.account_id = c.home_id))::uuid AS home_leg
      FROM candidates c
      JOIN public.txn_legs l ON l.header_id = c.header_id
     GROUP BY l.header_id
), tagged AS (
    SELECT c.header_id,
           EXISTS (SELECT 1 FROM public.txn_header_tags ht WHERE ht.header_id = c.header_id)
        OR EXISTS (SELECT 1 FROM public.txn_leg_tags lt JOIN public.txn_legs l ON l.id = lt.leg_id
                    WHERE l.header_id = c.header_id) AS any_tag
      FROM candidates c
)
SELECT c.*, g.postings, g.home_leg,
       c.header_memo IS NOT DISTINCT FROM c.written_old AND g.memos = 0 AS memo_ok,
       NOT t.any_tag AS tags_ok,
       (SELECT mig264_join_distinct(ARRAY[c.written_new]
                                    || array_agg(mig264_norm(s.descr) ORDER BY s.idx)
                                       FILTER (WHERE s.splittype IS DISTINCT FROM 'sec'
                                                 AND mig264_norm(s.descr) IS NOT NULL
                                                 AND mig264_norm(s.descr) IS DISTINCT FROM c.payee COLLATE "C"))
          FROM mig264_md_splits(c.payload, '') s) AS target_memo
  FROM candidates c
  JOIN legs g ON g.header_id = c.header_id
  JOIN tagged t ON t.header_id = c.header_id
 WHERE g.postings > 1 OR g.home_legs = 1;
ALTER TABLE mig264_inv ADD PRIMARY KEY (header_id);
ANALYZE mig264_inv;

DO $stepB$
DECLARE
    v_candidates bigint;
    v_memo       bigint;
    v_tags       bigint;
    v_applied    record;
BEGIN
    SELECT count(*), count(*) FILTER (WHERE memo_ok), count(*) FILTER (WHERE tags_ok)
      INTO v_candidates, v_memo, v_tags
      FROM mig264_inv;

    UPDATE public.txn_headers h
       SET memo = CASE WHEN i.postings > 1 THEN i.target_memo END
      FROM mig264_inv i
     WHERE h.id = i.header_id AND i.memo_ok
       AND h.memo IS DISTINCT FROM CASE WHEN i.postings > 1 THEN i.target_memo END;

    UPDATE public.txn_legs l
       SET leg_memo = i.target_memo
      FROM mig264_inv i
     WHERE l.id = i.home_leg AND i.memo_ok AND i.postings = 1
       AND l.leg_memo IS DISTINCT FROM i.target_memo;

    INSERT INTO mig264_retag (header_id) SELECT header_id FROM mig264_inv WHERE tags_ok;
    INSERT INTO mig264_target_tag (header_id, leg_id, ledger_id, name, ord, created_at)
    SELECT i.header_id, CASE WHEN i.postings = 1 THEN i.home_leg END, i.ledger_id, n.name, n.ord, i.created_at
      FROM mig264_inv i
     CROSS JOIN LATERAL unnest(
            mig264_tag_names(i.txn_tags, TRUE)
            || COALESCE((SELECT array_agg(u.nm ORDER BY s.idx, u.ord)
                           FROM mig264_md_splits(i.payload, '') s
                          CROSS JOIN LATERAL unnest(mig264_tag_names(s.tags, TRUE)) WITH ORDINALITY AS u(nm, ord)),
                        '{}'::text[])) WITH ORDINALITY AS n(name, ord)
     WHERE i.tags_ok;
    SELECT * INTO v_applied FROM mig264_apply_tags();

    RAISE NOTICE '264 step B: % Moneydance investment row(s) on their home brokerage; % memo(s) and % tag set(s) '
                 'rewritten, the rest edited and left as they are; % tag(s) created, % link(s) written',
                 v_candidates, v_memo, v_tags, v_applied.tags_created, v_applied.links_written;
END $stepB$;

ANALYZE public.txn_headers;
ANALYZE public.txn_legs;
ANALYZE public.tags;
ANALYZE public.txn_header_tags;
ANALYZE public.txn_leg_tags;
ANALYZE public.saved_reports;

-- ---------------------------------------------------------------------------
-- Step M. Tags whose names share a key (ADR-0108 D12). Within each ledger, the tags with
-- one tag_name_key are one tag from now on: the earliest created, then the lowest
-- id, survives (the pick every writer and steps A and B already resolve to). Every
-- transaction keeps the tag, now under the survivor's spelling; a link that would
-- duplicate one the row already has is dropped. Saved reports filtered on a merged
-- variant, every owner's, are repointed to the survivor before the variant goes. Then
-- the unique index makes a variant impossible.
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE mig264_m_map ON COMMIT DROP AS
WITH ranked AS (
    SELECT t.id, t.ledger_id,
           first_value(t.id) OVER (PARTITION BY t.ledger_id, public.tag_name_key(t.name)
                                   ORDER BY t.created_at, t.id) AS survivor_id
      FROM public.tags t
)
SELECT r.id AS variant_id, r.ledger_id, r.survivor_id
  FROM ranked r
 WHERE r.id <> r.survivor_id;
ALTER TABLE mig264_m_map ADD PRIMARY KEY (variant_id);
ANALYZE mig264_m_map;

DO $stepM$
DECLARE
    v_variants bigint;
    v_header   bigint;
    v_leg      bigint;
    v_reports  bigint;
BEGIN
    SELECT count(*) INTO v_variants FROM mig264_m_map;

    INSERT INTO public.txn_header_tags (header_id, tag_id, ledger_id, created_at)
    SELECT DISTINCT ON (ht.header_id, m.survivor_id) ht.header_id, m.survivor_id, ht.ledger_id, ht.created_at
      FROM public.txn_header_tags ht
      JOIN mig264_m_map m ON m.variant_id = ht.tag_id
     ORDER BY ht.header_id, m.survivor_id, ht.created_at, ht.tag_id
    ON CONFLICT (header_id, tag_id) DO NOTHING;
    GET DIAGNOSTICS v_header = ROW_COUNT;

    INSERT INTO public.txn_leg_tags (leg_id, tag_id, ledger_id, created_at)
    SELECT DISTINCT ON (lt.leg_id, m.survivor_id) lt.leg_id, m.survivor_id, lt.ledger_id, lt.created_at
      FROM public.txn_leg_tags lt
      JOIN mig264_m_map m ON m.variant_id = lt.tag_id
     ORDER BY lt.leg_id, m.survivor_id, lt.created_at, lt.tag_id
    ON CONFLICT (leg_id, tag_id) DO NOTHING;
    GET DIAGNOSTICS v_leg = ROW_COUNT;

    -- The join is on the variant id, unique in the map, so each report matches at most
    -- one row. updated_at is not touched: no owner edited the report.
    UPDATE public.saved_reports r
       SET spec = jsonb_set(r.spec, '{tagId}', to_jsonb(m.survivor_id::text))
      FROM mig264_m_map m
     WHERE m.ledger_id = r.ledger_id
       AND m.variant_id = public.saved_report_spec_tag_id(r.spec);
    GET DIAGNOSTICS v_reports = ROW_COUNT;

    -- The variants' own links, and any stored presentation naming them, cascade.
    DELETE FROM public.tags t USING mig264_m_map m WHERE t.id = m.variant_id;

    RAISE NOTICE '264 step M: % tag(s) merged into a same-named one; % header and % leg link(s) moved, '
                 '% saved report(s) repointed', v_variants, v_header, v_leg, v_reports;
END $stepM$;

CREATE UNIQUE INDEX IF NOT EXISTS uq_tags_name_key_per_ledger
    ON public.tags (ledger_id, public.tag_name_key(name));

COMMENT ON INDEX public.uq_tags_name_key_per_ledger IS
    'One tag per name per ledger under tag_name_key, the one definition of "the same name" '
    '(mig 263): case and surrounding white space never make a second tag (mig 264).';

ANALYZE public.tags;
ANALYZE public.txn_header_tags;
ANALYZE public.txn_leg_tags;

-- ---------------------------------------------------------------------------
-- Step C. Every remaining one-posting header with a memo or tags: native rows, the
-- Moneydance fields steps A and B left, templates and fired occurrences. X is the
-- originating account's leg, Y the posting's other leg, H the header. Before, a
-- side showed its own memo, else H's, and both sides showed H's tags.
--
--   X's memo   its own, else H. If both legs hold their own and H differs from both,
--              H was shown nowhere; it is joined onto X ("H · X"), counted.
--   Y's memo   its own; else H where X has its own (so Y keeps showing H), and
--              nothing where X takes H (Y then inherits it).
--   X's tags   H's and its own.
--   Y's tags   H's and its own, or none when neither leg had its own (Y inherits).
--
-- A one-posting leg's own tags were not shown before; they now are (ADR-0108 D16). The
-- header is cleared: a one-posting header holds nothing (ADR-0108 D1).
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE mig264_c ON COMMIT DROP AS
WITH one_posting AS (
    SELECT l.header_id
      FROM public.txn_legs l
     GROUP BY l.header_id
    HAVING count(DISTINCT l.posting_index) = 1
), held AS (
    SELECT h.id AS header_id, h.ledger_id, h.created_at, h.memo, h.originating_account_id
      FROM public.txn_headers h
      JOIN one_posting p ON p.header_id = h.id
     WHERE h.memo IS NOT NULL
        OR EXISTS (SELECT 1 FROM public.txn_header_tags ht WHERE ht.header_id = h.id)
)
SELECT d.header_id, d.ledger_id, d.created_at, d.memo AS h_memo,
       x.id AS x_leg, x.leg_memo AS x_memo,
       y.id AS y_leg, y.leg_memo AS y_memo
  FROM held d
  JOIN LATERAL (SELECT l.id, l.leg_memo FROM public.txn_legs l
                 WHERE l.header_id = d.header_id AND l.account_id = d.originating_account_id
                 ORDER BY l.id LIMIT 1) x ON TRUE
  LEFT JOIN LATERAL (SELECT l.id, l.leg_memo FROM public.txn_legs l
                      WHERE l.header_id = d.header_id AND l.id <> x.id
                      ORDER BY l.id LIMIT 1) y ON TRUE;
ALTER TABLE mig264_c ADD PRIMARY KEY (header_id);
ANALYZE mig264_c;

-- Whether each leg holds tags of its own.
CREATE TEMP TABLE mig264_c_own ON COMMIT DROP AS
SELECT c.header_id,
       EXISTS (SELECT 1 FROM public.txn_leg_tags lt WHERE lt.leg_id = c.x_leg) AS x_tags,
       c.y_leg IS NOT NULL AND EXISTS (SELECT 1 FROM public.txn_leg_tags lt WHERE lt.leg_id = c.y_leg) AS y_tags
  FROM mig264_c c;
ALTER TABLE mig264_c_own ADD PRIMARY KEY (header_id);
ANALYZE mig264_c_own;

DO $stepC$
DECLARE
    v_headers bigint;
    v_joined  bigint;
    v_n       bigint;
BEGIN
    SELECT count(*),
           count(*) FILTER (WHERE x_memo IS NOT NULL AND y_memo IS NOT NULL AND h_memo IS NOT NULL
                              AND h_memo <> x_memo COLLATE "C" AND h_memo <> y_memo COLLATE "C")
      INTO v_headers, v_joined
      FROM mig264_c;

    UPDATE public.txn_legs l
       SET leg_memo = CASE
             WHEN c.x_memo IS NOT NULL AND c.y_memo IS NOT NULL AND c.h_memo IS NOT NULL
              AND c.h_memo <> c.x_memo COLLATE "C" AND c.h_memo <> c.y_memo COLLATE "C"
               THEN mig264_join(c.h_memo, c.x_memo)
             ELSE COALESCE(c.x_memo, c.h_memo)
           END
      FROM mig264_c c
     WHERE l.id = c.x_leg
       AND l.leg_memo IS DISTINCT FROM CASE
             WHEN c.x_memo IS NOT NULL AND c.y_memo IS NOT NULL AND c.h_memo IS NOT NULL
              AND c.h_memo <> c.x_memo COLLATE "C" AND c.h_memo <> c.y_memo COLLATE "C"
               THEN mig264_join(c.h_memo, c.x_memo)
             ELSE COALESCE(c.x_memo, c.h_memo)
           END;

    UPDATE public.txn_legs l
       SET leg_memo = COALESCE(c.y_memo, CASE WHEN c.x_memo IS NULL THEN NULL ELSE c.h_memo END)
      FROM mig264_c c
     WHERE l.id = c.y_leg
       AND l.leg_memo IS DISTINCT FROM COALESCE(c.y_memo, CASE WHEN c.x_memo IS NULL THEN NULL ELSE c.h_memo END);

    -- H's tags onto X, and onto Y where either leg had its own. A link moved off the
    -- header keeps its created_at.
    INSERT INTO public.txn_leg_tags (leg_id, tag_id, ledger_id, created_at)
    SELECT c.x_leg, ht.tag_id, ht.ledger_id, ht.created_at
      FROM mig264_c c
      JOIN public.txn_header_tags ht ON ht.header_id = c.header_id
    ON CONFLICT (leg_id, tag_id) DO NOTHING;

    INSERT INTO public.txn_leg_tags (leg_id, tag_id, ledger_id, created_at)
    SELECT c.y_leg, ht.tag_id, ht.ledger_id, ht.created_at
      FROM mig264_c c
      JOIN mig264_c_own o ON o.header_id = c.header_id
      JOIN public.txn_header_tags ht ON ht.header_id = c.header_id
     WHERE c.y_leg IS NOT NULL AND (o.x_tags OR o.y_tags)
    ON CONFLICT (leg_id, tag_id) DO NOTHING;

    DELETE FROM public.txn_header_tags ht USING mig264_c c WHERE ht.header_id = c.header_id;
    GET DIAGNOSTICS v_n = ROW_COUNT;

    UPDATE public.txn_headers h SET memo = NULL
      FROM mig264_c c
     WHERE h.id = c.header_id AND h.memo IS NOT NULL;

    RAISE NOTICE '264 step C: % one-posting header(s) moved onto their legs (% header tag link(s)); '
                 '% memo(s) shown on neither side joined onto the originating side', v_headers, v_n, v_joined;
END $stepC$;

ANALYZE public.txn_headers;
ANALYZE public.txn_legs;
ANALYZE public.txn_header_tags;
ANALYZE public.txn_leg_tags;

-- ---------------------------------------------------------------------------
-- Step D. Multi-posting investment events with values on legs of their investment
-- side (the originating brokerage and its holdings account; for an in-kind transfer
-- every brokerage on it and theirs). The investment register showed one row per
-- event: its first leg on the brokerage's own memo, else the header's, and the
-- header's tags. The event keeps one memo and one tag set, on the header:
--
--   memo  the header's, else that first leg's; then every other memo on the
--         investment side's legs not already part of it, joined with " · " in
--         posting order, so no text is lost;
--   tags  the header's and every tag on those legs.
--
-- Those legs are then cleared; they never carry values (ADR-0108 D1). A funding
-- brokerage and the other legs are not the investment side and are left alone. An
-- action-less multi-split on a brokerage is not an event and is not touched.
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE mig264_d_side ON COMMIT DROP AS
WITH events AS (
    SELECT h.id AS header_id, h.action, h.originating_account_id
      FROM public.txn_headers h
     WHERE h.action IS NOT NULL
       AND (SELECT count(DISTINCT l.posting_index) FROM public.txn_legs l WHERE l.header_id = h.id) > 1
), side_accounts AS (
    SELECT e.header_id, e.originating_account_id AS account_id
      FROM events e WHERE e.action <> 'transfer_shares' AND e.originating_account_id IS NOT NULL
    UNION
    SELECT e.header_id, a.holdings_account_id
      FROM events e JOIN public.accounts a ON a.id = e.originating_account_id
     WHERE e.action <> 'transfer_shares' AND a.holdings_account_id IS NOT NULL
    UNION
    SELECT e.header_id, a.id
      FROM events e
      JOIN public.txn_legs l ON l.header_id = e.header_id
      JOIN public.accounts a ON a.id = l.account_id
     WHERE e.action = 'transfer_shares' AND a.holdings_account_id IS NOT NULL
    UNION
    SELECT e.header_id, a.holdings_account_id
      FROM events e
      JOIN public.txn_legs l ON l.header_id = e.header_id
      JOIN public.accounts a ON a.id = l.account_id
     WHERE e.action = 'transfer_shares' AND a.holdings_account_id IS NOT NULL
)
SELECT l.header_id, l.id AS leg_id, l.posting_index, l.leg_memo,
       l.account_id = e.originating_account_id AS on_owner
  FROM side_accounts s
  JOIN events e ON e.header_id = s.header_id
  JOIN public.txn_legs l ON l.header_id = s.header_id AND l.account_id = s.account_id;
ALTER TABLE mig264_d_side ADD PRIMARY KEY (leg_id);
ANALYZE mig264_d_side;

CREATE TEMP TABLE mig264_d ON COMMIT DROP AS
SELECT s.header_id
  FROM mig264_d_side s
 WHERE s.leg_memo IS NOT NULL
    OR EXISTS (SELECT 1 FROM public.txn_leg_tags lt WHERE lt.leg_id = s.leg_id)
 GROUP BY s.header_id;
ALTER TABLE mig264_d ADD PRIMARY KEY (header_id);
ANALYZE mig264_d;

DO $stepD$
DECLARE
    v_events bigint;
    v_legs   bigint;
BEGIN
    SELECT count(*) INTO v_events FROM mig264_d;

    UPDATE public.txn_headers h
       SET memo = x.memo
      FROM (SELECT d.header_id,
                   mig264_join_distinct(ARRAY[hh.memo] || COALESCE(
                       (SELECT array_agg(p.leg_memo ORDER BY p.is_first DESC, p.posting_index, p.leg_id)
                          FROM (SELECT s.leg_memo, s.posting_index, s.leg_id,
                                       s.leg_id = (SELECT s2.leg_id FROM mig264_d_side s2
                                                    WHERE s2.header_id = s.header_id AND s2.on_owner
                                                    ORDER BY s2.posting_index, s2.leg_id LIMIT 1) AS is_first
                                  FROM mig264_d_side s
                                 WHERE s.header_id = d.header_id AND s.leg_memo IS NOT NULL) p),
                       '{}'::text[])) AS memo
              FROM mig264_d d
              JOIN public.txn_headers hh ON hh.id = d.header_id) x
     WHERE h.id = x.header_id
       AND h.memo IS DISTINCT FROM x.memo;

    INSERT INTO public.txn_header_tags (header_id, tag_id, ledger_id, created_at)
    SELECT DISTINCT ON (s.header_id, lt.tag_id) s.header_id, lt.tag_id, lt.ledger_id, lt.created_at
      FROM mig264_d d
      JOIN mig264_d_side s ON s.header_id = d.header_id
      JOIN public.txn_leg_tags lt ON lt.leg_id = s.leg_id
     ORDER BY s.header_id, lt.tag_id, lt.created_at, lt.leg_id
    ON CONFLICT (header_id, tag_id) DO NOTHING;

    DELETE FROM public.txn_leg_tags lt
     USING mig264_d_side s, mig264_d d
     WHERE lt.leg_id = s.leg_id AND s.header_id = d.header_id;

    UPDATE public.txn_legs l SET leg_memo = NULL
      FROM mig264_d_side s, mig264_d d
     WHERE l.id = s.leg_id AND s.header_id = d.header_id AND l.leg_memo IS NOT NULL;
    GET DIAGNOSTICS v_legs = ROW_COUNT;

    RAISE NOTICE '264 step D: % multi-posting event(s) had values on their investment side; '
                 'joined onto the event, % leg memo(s) cleared', v_events, v_legs;
END $stepD$;

ANALYZE public.txn_headers;
ANALYZE public.txn_legs;
ANALYZE public.txn_header_tags;
ANALYZE public.txn_leg_tags;

-- ---------------------------------------------------------------------------
-- End. The invariants (ADR-0108 D11), over every header of every ledger. A violation
-- fails the upgrade and, this being one transaction, leaves the database as it was.
--
--   (a) a one-posting header holds no memo and no tags;
--   (b) the legs of an actioned multi-posting event's investment side hold nothing;
--   (c) the originating account is on a leg of its header, NULL exactly when it has
--       none (263's clause a);
--   (d) an actioned event other than an in-kind transfer is owned by a brokerage
--       with a leg in every posting; (e) an in-kind transfer by its source brokerage;
--   (f) an action-less multi-posting header on which some account has a leg in
--       every posting is owned by one of them;
--   (g) no stored memo is blank;
--   (h) no two tags of a ledger share a key;
--   (i) no saved report names a tag of its ledger that step M merged away.
--
-- Each fact is aggregated once over the table and joined to its header by key.
-- ---------------------------------------------------------------------------
DO $assert$
DECLARE
    v_a bigint; v_b bigint; v_c bigint; v_d bigint; v_e bigint;
    v_f bigint; v_g bigint; v_h bigint; v_i bigint;
BEGIN
    WITH per_account AS (
        SELECT l.header_id, l.account_id, count(DISTINCT l.posting_index) AS postings_on
          FROM public.txn_legs l
         GROUP BY l.header_id, l.account_id
    ), shape AS (
        SELECT l.header_id, count(DISTINCT l.posting_index) AS postings
          FROM public.txn_legs l
         GROUP BY l.header_id
    ), widest AS (
        SELECT p.header_id, max(p.postings_on) AS most_on
          FROM per_account p
         GROUP BY p.header_id
    ), header_tags AS (
        SELECT ht.header_id, count(*) AS n FROM public.txn_header_tags ht GROUP BY ht.header_id
    ), sources AS (
        SELECT DISTINCT s.header_id, s.account_id
          FROM public.ledgers g
         CROSS JOIN LATERAL public.transfer_shares_source_brokerages(g.id) s
    ), facts AS (
        SELECT h.action,
               h.memo IS NOT NULL                          AS has_memo,
               COALESCE(t.n, 0) > 0                        AS has_tags,
               h.originating_account_id IS NOT NULL        AS has_owner,
               s.header_id IS NOT NULL                     AS has_legs,
               COALESCE(s.postings, 0)                     AS postings,
               COALESCE(w.most_on = s.postings, FALSE)     AS some_on_every,
               o.header_id IS NOT NULL                     AS owner_on_leg,
               COALESCE(o.postings_on = s.postings, FALSE) AS owner_on_every,
               a.holdings_account_id IS NOT NULL           AS owner_is_brokerage,
               x.header_id IS NOT NULL                     AS owner_is_source
          FROM public.txn_headers h
          LEFT JOIN shape s       ON s.header_id = h.id
          LEFT JOIN widest w      ON w.header_id = h.id
          LEFT JOIN header_tags t ON t.header_id = h.id
          LEFT JOIN per_account o ON o.header_id = h.id AND o.account_id = h.originating_account_id
          LEFT JOIN public.accounts a ON a.id = h.originating_account_id
          LEFT JOIN sources x     ON x.header_id = h.id AND x.account_id = h.originating_account_id
    )
    SELECT count(*) FILTER (WHERE f.postings = 1 AND (f.has_memo OR f.has_tags)),
           count(*) FILTER (WHERE (NOT f.has_owner AND f.has_legs) OR (f.has_owner AND NOT f.owner_on_leg)),
           count(*) FILTER (WHERE f.action IS NOT NULL AND f.action <> 'transfer_shares' AND f.has_legs
                              AND NOT (f.owner_on_every AND f.owner_is_brokerage)),
           count(*) FILTER (WHERE f.action = 'transfer_shares' AND f.has_legs AND NOT f.owner_is_source),
           count(*) FILTER (WHERE f.action IS NULL AND f.postings > 1 AND f.some_on_every AND NOT f.owner_on_every)
      INTO v_a, v_c, v_d, v_e, v_f
      FROM facts f;

    -- (b), over the investment side as step D reads it.
    WITH events AS (
        SELECT h.id AS header_id, h.action, h.originating_account_id
          FROM public.txn_headers h
          JOIN (SELECT l.header_id FROM public.txn_legs l
                 GROUP BY l.header_id HAVING count(DISTINCT l.posting_index) > 1) m ON m.header_id = h.id
         WHERE h.action IS NOT NULL
    ), side_accounts AS (
        SELECT e.header_id, e.originating_account_id AS account_id
          FROM events e WHERE e.action <> 'transfer_shares'
        UNION
        SELECT e.header_id, a.holdings_account_id
          FROM events e JOIN public.accounts a ON a.id = e.originating_account_id
         WHERE e.action <> 'transfer_shares' AND a.holdings_account_id IS NOT NULL
        UNION
        SELECT e.header_id, a.id
          FROM events e JOIN public.txn_legs l ON l.header_id = e.header_id JOIN public.accounts a ON a.id = l.account_id
         WHERE e.action = 'transfer_shares' AND a.holdings_account_id IS NOT NULL
        UNION
        SELECT e.header_id, a.holdings_account_id
          FROM events e JOIN public.txn_legs l ON l.header_id = e.header_id JOIN public.accounts a ON a.id = l.account_id
         WHERE e.action = 'transfer_shares' AND a.holdings_account_id IS NOT NULL
    )
    SELECT count(DISTINCT l.header_id) INTO v_b
      FROM side_accounts s
      JOIN public.txn_legs l ON l.header_id = s.header_id AND l.account_id = s.account_id
     WHERE l.leg_memo IS NOT NULL
        OR EXISTS (SELECT 1 FROM public.txn_leg_tags lt WHERE lt.leg_id = l.id);

    SELECT (SELECT count(*) FROM public.txn_headers WHERE memo IS NOT NULL AND mig264_is_blank(memo))
         + (SELECT count(*) FROM public.txn_legs WHERE leg_memo IS NOT NULL AND mig264_is_blank(leg_memo))
      INTO v_g;

    SELECT count(*) INTO v_h
      FROM (SELECT 1 FROM public.tags GROUP BY ledger_id, public.tag_name_key(name) HAVING count(*) > 1) k;

    -- Within the report's own ledger: a report naming another ledger's tag never
    -- named one of its own, and step M leaves it as it was.
    SELECT count(*) INTO v_i
      FROM public.saved_reports r
      JOIN mig264_m_map m ON m.variant_id = public.saved_report_spec_tag_id(r.spec) AND m.ledger_id = r.ledger_id;

    IF v_a + v_b + v_c + v_d + v_e + v_f + v_g + v_h + v_i > 0 THEN
        RAISE EXCEPTION
            'Migration 264 found data that breaks an invariant: (a) one-posting header with a memo or tags: %; '
            '(b) investment-side leg of a multi-posting event with a memo or tags: %; (c) originating account '
            'not on a leg, or NULL with legs: %; (d) actioned event not owned by a brokerage on every posting: %; '
            '(e) in-kind transfer not owned by its source brokerage: %; (f) multi-split not owned by an account '
            'on every line: %; (g) blank memo: %; (h) tags sharing a name key: %; (i) saved report on a merged '
            'tag: %. Nothing was changed; repair those transactions, then re-run the upgrade.',
            v_a, v_b, v_c, v_d, v_e, v_f, v_g, v_h, v_i;
    END IF;
END $assert$;

DROP FUNCTION mig264_apply_tags();
DROP FUNCTION mig264_join_distinct(text[]);
DROP FUNCTION mig264_md_is_investment_shape(jsonb, text);
DROP FUNCTION mig264_md_splits(jsonb, text);
DROP FUNCTION mig264_tag_names(text, boolean);
DROP FUNCTION mig264_utf16_length(text);
DROP FUNCTION mig264_utf16_left(text, integer);
DROP FUNCTION mig264_join(text, text);
DROP FUNCTION mig264_norm(text);
DROP FUNCTION mig264_blank_to_null(text);
DROP FUNCTION mig264_is_blank(text);
DROP FUNCTION mig264_trim_end(text);
DROP FUNCTION mig264_trim(text);
DROP FUNCTION mig264_md_text(jsonb, text);

COMMIT;
