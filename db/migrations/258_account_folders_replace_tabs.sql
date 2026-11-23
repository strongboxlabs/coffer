-- =============================================================================
-- 258 — account folders replace sidebar tabs
-- =============================================================================
--
-- WHY
-- ---
-- The sidebar's tabs (mig 033: user_account_groups + user_account_group_members)
-- were per-user FILTERS over the account list: a tab showed some accounts and hid
-- the rest. They never held financial data, and nothing outside the sidebar read
-- them. Folders replace them (FU-006): a named, nestable grouping of real
-- accounts that every member of the ledger sees, shown in the sidebar with the sum
-- of what it holds. Dropping the tab tables loses only those filters.
--
-- WHY A SEPARATE TABLE, NOT AN accounts ROW
-- ----------------------------------------
-- A folder holds no transactions and has no balance of its own, so it is not an
-- account. Modelled as an accounts row (the backlog's first option, an is_folder
-- flag) it would appear in every picker, register and report that lists accounts,
-- and each of those would need a filter it does not have today. A table of its own
-- keeps every existing reader of accounts correct with no change.
--
-- WHY THE TYPE IS IN THE FOREIGN KEYS
-- -----------------------------------
-- A folder belongs to ONE account type: it sits inside that type's section of the
-- sidebar, and only accounts of that type go in it. Both halves are database facts
-- rather than repository checks:
--
--   * account_folder_members references accounts(id, ledger_id, account_type) AND
--     account_folders(id, ledger_id, account_type) through the same account_type
--     column, so a member's account and its folder cannot disagree on the type.
--     That needs a new UNIQUE (id, ledger_id, account_type) on accounts to compose
--     against; id alone is already unique, so it costs an index and refuses
--     nothing that exists. It also pins the type of an account that sits in a
--     folder: an UPDATE of accounts.account_type on such an account is refused
--     (the type is immutable through the API anyway).
--   * a folder's parent is referenced as (parent_id, ledger_id, account_type), so a
--     sub-folder always has its parent's type and ledger.
--
-- One folder per account is the members table's primary key on account_id.
--
-- NESTING AND CYCLES
-- ------------------
-- Folders nest to any depth (no cap). A folder may not become
-- its own ancestor; that is a recursive predicate no CHECK or index can express,
-- so a trigger refuses it (fn_account_folders_refuse_cycle). The API checks first
-- and answers with a business error; the trigger is the backstop for a race or a
-- caller that skips the API. Its error carries the constraint name
-- ck_account_folders_acyclic, which the API maps to the same business error.
--
-- The parent reference is ON DELETE CASCADE, but the API never relies on it: a
-- deleted folder's contents move up to its parent first, in the same transaction,
-- so the cascade only fires for a whole-ledger delete or a snapshot clear, where
-- every folder goes anyway.
--
-- The parent reference is DEFERRABLE INITIALLY DEFERRED for the reason mig 247
-- gave its own self-references: a v3 restore replays a table in chunks keyset-
-- ordered on id, one INSERT per chunk, so a sub-folder can land in an earlier chunk
-- than its parent. Referential ACTIONS are never deferred, so the cascade still
-- fires at once.
--
-- ROLES
-- -----
-- Folders are ledger data, not per-user view state, so they take the role-aware
-- pair (read for any grant, write for owner and editor) that mig 225 used for
-- budget_targets, and explicit grants since mig 254 removed the default ones.
--
-- SNAPSHOTS
-- ---------
-- Both tables are captured. The six functions mig 247 owns are re-declared whole
-- below, copied from their live definitions (247 is still their last owner, and
-- 257 re-issued none of them), with the tab tables taken out and the folder tables
-- put in: folders after accounts, members after both. The new migration number is
-- the new schema version, so every existing snapshot is retired (ADR-0037).
-- =============================================================================

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Tabs go.
-- ---------------------------------------------------------------------------
DROP TABLE user_account_group_members;
DROP TABLE user_account_groups;

-- ---------------------------------------------------------------------------
-- 2. The key the type-matching references compose against.
-- ---------------------------------------------------------------------------
ALTER TABLE accounts
    ADD CONSTRAINT uq_accounts_id_ledger_type UNIQUE (id, ledger_id, account_type);

-- ---------------------------------------------------------------------------
-- 3. account_folders.
-- ---------------------------------------------------------------------------
CREATE TABLE account_folders (
    -- Called exactly `id` and a uuid: the snapshot writer chunks on it (mig 225
    -- explains why a table without one falls back to the unchunked path).
    id            UUID         NOT NULL DEFAULT gen_random_uuid(),
    ledger_id     UUID         NOT NULL,

    -- The six real account types (the accounts CHECK of mig 007, without
    -- 'category'). A folder of categories is not a thing: the category tree is
    -- accounts.parent_id.
    account_type  TEXT         NOT NULL,

    name          TEXT         NOT NULL,

    -- NULL is the top level of the type's section.
    parent_id     UUID,

    created_at    TIMESTAMPTZ  NOT NULL DEFAULT now(),

    CONSTRAINT pk_account_folders PRIMARY KEY (id),

    -- The target of the member and parent references.
    CONSTRAINT uq_account_folders_id_ledger_type UNIQUE (id, ledger_id, account_type),

    CONSTRAINT ck_account_folders_account_type CHECK (account_type IN (
        'bank', 'credit_card', 'investment',
        'asset', 'liability', 'loan'
    )),

    CONSTRAINT ck_account_folders_name_not_blank CHECK (btrim(name) <> ''),
    CONSTRAINT ck_account_folders_name_length    CHECK (char_length(name) <= 80),

    CONSTRAINT fk_account_folders_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE,

    CONSTRAINT fk_account_folders_parent
        FOREIGN KEY (parent_id, ledger_id, account_type)
        REFERENCES account_folders(id, ledger_id, account_type)
        ON DELETE CASCADE
        DEFERRABLE INITIALLY DEFERRED
);

-- Names are unique among siblings, case-insensitively. NULLS NOT DISTINCT so two
-- top-level folders of one type collide as well. The leading columns also serve
-- the parent reference's lookups (the children of a folder).
CREATE UNIQUE INDEX uq_account_folders_sibling_name
    ON account_folders (ledger_id, account_type, parent_id, lower(name))
    NULLS NOT DISTINCT;

-- ---------------------------------------------------------------------------
-- 4. The cycle guard.
-- ---------------------------------------------------------------------------
-- Walks the ancestors of the new parent; reaching the row itself is a cycle. The
-- per-ledger advisory lock serialises moves, so two concurrent moves cannot each
-- pass against a state the other is about to change (each statement here reads a
-- fresh snapshot once the lock is held). A parent that is not there yet, as in a
-- restore replaying chunks, ends the walk: the deferred reference checks it later.
CREATE FUNCTION fn_account_folders_refuse_cycle()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $$
DECLARE
    v_cursor uuid := NEW.parent_id;
BEGIN
    IF NEW.parent_id IS NULL THEN
        RETURN NEW;
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended('coffer.account-folders:' || NEW.ledger_id::text, 0));

    WHILE v_cursor IS NOT NULL LOOP
        IF v_cursor = NEW.id THEN
            RAISE EXCEPTION 'account folder % cannot be placed inside itself or one of its own sub-folders', NEW.id
                USING ERRCODE = 'check_violation', CONSTRAINT = 'ck_account_folders_acyclic';
        END IF;
        -- No row leaves v_cursor NULL, which ends the walk.
        SELECT f.parent_id INTO v_cursor FROM account_folders f WHERE f.id = v_cursor;
    END LOOP;

    RETURN NEW;
END;
$$;

GRANT EXECUTE ON FUNCTION fn_account_folders_refuse_cycle() TO coffer_app;

CREATE TRIGGER trg_account_folders_refuse_cycle
    BEFORE INSERT OR UPDATE OF parent_id ON account_folders
    FOR EACH ROW EXECUTE FUNCTION fn_account_folders_refuse_cycle();

-- ---------------------------------------------------------------------------
-- 5. account_folder_members: which folder an account sits in.
-- ---------------------------------------------------------------------------
CREATE TABLE account_folder_members (
    -- One folder per account. An account in no folder has no row.
    account_id    UUID  NOT NULL,
    ledger_id     UUID  NOT NULL,
    folder_id     UUID  NOT NULL,
    -- The column both references share, which is what makes "the account's type
    -- is the folder's type" true by construction.
    account_type  TEXT  NOT NULL,

    CONSTRAINT pk_account_folder_members PRIMARY KEY (account_id),

    -- CASCADE for the reason mig 225 gives: snapshot clear and ledger delete both
    -- delete every accounts row, and RESTRICT would break both.
    CONSTRAINT fk_account_folder_members_account
        FOREIGN KEY (account_id, ledger_id, account_type)
        REFERENCES accounts(id, ledger_id, account_type) ON DELETE CASCADE,

    CONSTRAINT fk_account_folder_members_folder
        FOREIGN KEY (folder_id, ledger_id, account_type)
        REFERENCES account_folders(id, ledger_id, account_type) ON DELETE CASCADE
);

-- A folder's members, for the listing and for the folder reference's lookups.
CREATE INDEX ix_account_folder_members_folder
    ON account_folder_members (folder_id, ledger_id, account_type);

-- ---------------------------------------------------------------------------
-- 6. RLS and grants: the mig 225 pair, on both tables.
-- ---------------------------------------------------------------------------
ALTER TABLE account_folders ENABLE ROW LEVEL SECURITY;

CREATE POLICY account_folders_read
    ON account_folders FOR SELECT TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

CREATE POLICY account_folders_write
    ON account_folders FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
               AND role IN ('owner', 'editor')
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
               AND role IN ('owner', 'editor')
        )
    );

GRANT SELECT, INSERT, UPDATE, DELETE ON account_folders TO coffer_app;

ALTER TABLE account_folder_members ENABLE ROW LEVEL SECURITY;

CREATE POLICY account_folder_members_read
    ON account_folder_members FOR SELECT TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

CREATE POLICY account_folder_members_write
    ON account_folder_members FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
               AND role IN ('owner', 'editor')
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
               AND role IN ('owner', 'editor')
        )
    );

GRANT SELECT, INSERT, UPDATE, DELETE ON account_folder_members TO coffer_app;

-- ---------------------------------------------------------------------------
-- 7. The snapshot and delete bodies, re-emitted from mig 247 with the tab tables
--    replaced by the folder tables.
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
        'tags', 'txn_header_tags', 'txn_leg_tags', 'provider_security_mappings',
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
        'txn_header_originals', 'txn_header_tags', 'txn_leg_tags',
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

COMMIT;
