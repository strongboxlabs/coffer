-- ---------------------------------------------------------------------------
-- 243 — txn_leg_tags: a tag on one posting, not just on the transaction
--
-- ADR-0103. ADR-0009 put tags at transaction level and named its own reopening
-- condition in its alternatives: "Rejected unless real use surfaces a
-- split-level tag pattern that matters." A bill split across several rental
-- units is that pattern — one header, one posting per unit, and the only
-- question the tags exist to answer is which posting the money went to.
--
-- BOTH LEVELS, and they mean different things. txn_header_tags is untouched: a
-- header tag is a claim about the transaction (Holiday, Tax 2026), a leg tag a
-- claim about one posting (Property B). Not ADR-0009's own union model, which
-- would make a transaction tag a derived artifact that changes silently when a
-- leg is added later, and would force a rewrite of every header tag that is
-- correct today. This migration moves no existing row.
--
-- THE SHAPE IS FORCED, not designed. It copies migration 072 exactly:
--
--   * ledger_id NOT NULL, because fn_snapshot_write_part chunks on a generic
--     `WHERE t.ledger_id = $1`. A snapshot-captured table without that column
--     cannot be captured at all.
--   * composite FKs to txn_legs(id, ledger_id) and tags(id, ledger_id). These
--     are what make the denormalized ledger_id provably equal to the parent's,
--     which is the entire justification for the flat RLS predicate below —
--     without them `ledger_id IN (visible)` would be an assertion rather than a
--     consequence. Both parents already carry the composite UNIQUE these need
--     (txn_legs from mig 049, tags from mig 072), so no parent changes.
--   * the flattened user_ledger_grants policy, the same one txn_header_tags has
--     carried since 072.
--
-- The snapshot decision lands here rather than later because
-- SchemaDriftGuardTests fails the moment a ledger_id-bearing table is neither
-- captured nor excluded. txn_leg_tags IS ledger data and is captured, so six
-- bodies below are re-emitted: part_names, insert_order, payload, restore,
-- clear, and fn_ledger_delete.
--
-- ---------------------------------------------------------------------------
-- WHAT IS DELETED FROM THOSE BODIES, AND WHY
--
-- Three branches justified solely by "a payload captured before migration NNN"
-- are not carried forward. They cannot run. LedgerSnapshotsRepository.RestoreAsync
-- compares a snapshot's schema_version against the LATEST migration name and
-- refuses on mismatch (ADR-0037 Phase 1), so applying this migration invalidates
-- every snapshot captured before it — and both SQL restore entry points sit
-- behind that one guard. A payload old enough to need these branches cannot
-- reach them.
--
--   * the realized_gains NULL guard ("pre-mig-188 payload?"), whose own text
--     hedged on "if the schema-version guard was relaxed". It was not.
--   * the budget_targets COALESCE ("a payload captured before mig 225 has no
--     budget_targets key at all").
--   * the ~40-line pre-230 txn_header_overrides fold.
--
-- The fold had a test, and the test is deleted with it: SnapshotPre230RestoreTests
-- reached the branch by calling fn_ledger_snapshot_restore directly with a
-- hand-built payload — stated in its own helper comment — which is the only way
-- to reach it. That is a green test standing over a branch production cannot
-- enter. The guard it went around is itself covered end-to-end by
-- SnapshotsTests.Restore_refuses_with_schema_version_mismatch_when_snapshot_is_stale,
-- through the real endpoint.
--
-- These bodies are re-emitted verbatim by every migration that touches any of
-- them, so dead branches are not free: each one is copied forward, grows the
-- block a future author has to transcribe correctly, and reads as live
-- behaviour to anyone who has not traced the guard.
-- ---------------------------------------------------------------------------

CREATE TABLE txn_leg_tags (
    leg_id      uuid        NOT NULL,
    tag_id      uuid        NOT NULL,
    ledger_id   uuid        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT pk_txn_leg_tags PRIMARY KEY (leg_id, tag_id),

    -- Composite, both of them. A single-column FK would let a leg in one ledger
    -- carry a tag from another's vocabulary, and would leave ledger_id as an
    -- unverified copy rather than a derived fact.
    CONSTRAINT fk_txn_leg_tags_leg
        FOREIGN KEY (leg_id, ledger_id) REFERENCES txn_legs(id, ledger_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_txn_leg_tags_tag
        FOREIGN KEY (tag_id, ledger_id) REFERENCES tags(id, ledger_id)
        ON DELETE CASCADE
);

COMMENT ON TABLE txn_leg_tags IS
    'Tags on ONE posting (ADR-0103). Distinct from txn_header_tags, which tags '
    'the whole transaction — neither is derived from the other.';

-- "Which legs carry this tag?" is the filter direction the PK cannot serve: it
-- leads on leg_id, so a tag-first lookup would scan. Ledger-first because every
-- query is ledger-scoped before it is anything else.
CREATE INDEX ix_txn_leg_tags_ledger_tag ON txn_leg_tags (ledger_id, tag_id);

ALTER TABLE txn_leg_tags ENABLE ROW LEVEL SECURITY;

CREATE POLICY txn_leg_tags_per_user ON txn_leg_tags
    FOR ALL TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                              WHERE ulg.user_id = current_app_user_id()));

-- ---------------------------------------------------------------------------
-- The six snapshot / delete bodies, re-emitted.
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
        'tags', 'txn_header_tags', 'txn_leg_tags', 'provider_security_mappings',
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
        'txn_header_originals', 'txn_header_tags', 'txn_leg_tags'
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
        'txn_leg_tags',                     (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_tags t WHERE t.ledger_id = p_ledger_id),
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
    PERFORM fn_ledger_snapshot_clear(p_ledger_id);

    -- ----- Insert rows from the payload (forward-FK order) --------------
    INSERT INTO accounts                   SELECT * FROM jsonb_populate_recordset(NULL::accounts,                   v_payload->'accounts');
    INSERT INTO loan_terms                 SELECT * FROM jsonb_populate_recordset(NULL::loan_terms,                 v_payload->'loan_terms');
    INSERT INTO budget_targets             SELECT * FROM jsonb_populate_recordset(NULL::budget_targets,             v_payload->'budget_targets');
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
    INSERT INTO txn_leg_tags               SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_tags,               v_payload->'txn_leg_tags');

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
    DELETE FROM txn_leg_tags               WHERE ledger_id = p_ledger_id;
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
    DELETE FROM txn_leg_tags                 WHERE ledger_id = p_ledger_id;
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
