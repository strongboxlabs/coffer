-- =============================================================================
-- 225 — budget_targets: the number a person typed, as opposed to the one we
--       derived for them.
-- =============================================================================
--
-- ADR-0099 D1 and its D1a amendment. A budget row's mark is the user's target for
-- that category and month when one exists, and the derived trailing-months normal
-- when it does not. This table is the "when one exists" half; the normal needs no
-- storage because it is computed from the postings.
--
-- ONE ROW IS ONE DECISION. Presence of a row IS the state (ADR-0099 D1), per
-- category and month — not a per-ledger mode, not a flag. Deleting the row does not
-- clear the budget, it returns that category to its normal, which is why the screen
-- keeps working for someone who sets three categories and forgets the rest.
--
-- ===== WHY id IS A UUID CALLED EXACTLY `id` =====
--
-- Not taste. mig 197 (snapshot chunk keyset pagination) probes pg_attribute for an
-- attribute named exactly `id` and, finding one, paginates with
-- `(v_chunk -> -1 ->> 'id')::uuid`. A bigint or text `id` fails operator resolution
-- on the FIRST chunk of the FIRST capture — loudly, at least. Worse is having NO
-- `id` column: the function silently falls back to the unchunked path, which
-- carries a 500k-row RAISE ceiling. So the surrogate key is load-bearing
-- infrastructure, even though (ledger_id, category_id, target_month) is the real
-- identity and carries its own UNIQUE.
--
-- ===== THE TWO FOREIGN KEYS, AND WHY BOTH CASCADE =====
--
-- The category FK is COMPOSITE — (category_id, ledger_id) -> accounts(id, ledger_id),
-- resolving against uq_accounts_id_ledger — which is this repo's Phase A
-- ledger-coherence pattern (see fk_loan_terms_account, and the same shape on
-- holdings, txn_legs and account_external_ids). A plain REFERENCES accounts(id)
-- would let a row name a category in a DIFFERENT ledger than its own ledger_id.
--
-- The separate ledgers FK is deliberate and not redundant: it is the documented
-- Phase A anchor (docs/database-schema.md on loan_terms), and it is what makes
-- ledger deletion work.
--
-- CASCADE on both is FORCED by two independent paths, neither of which is optional:
--
--   * fn_ledger_snapshot_clear deletes every accounts row on EVERY restore, and
--   * fn_ledger_delete does the same on every ledger delete.
--
-- Under RESTRICT, both of those break — and no CI guard covers fn_ledger_delete's
-- completeness, so it would ship green and then fail DELETE /api/ledgers/{id} in
-- production. SET NULL is not available either: it would null a NOT NULL column.
--
-- ===== NUMERIC(19,2), AND ROUNDING EXACTLY ONCE =====
--
-- There is no single money scale in this schema — realized_gains is (19,2),
-- txn_legs.amount is (19,4) WITH a 2dp CHECK, txn_leg_overrides.amount is (19,4)
-- with no CHECK and holds sub-cent values today, loan_terms is (20,4). The line in
-- docs/database-schema.md calling (19,4) the convention is stale as a universal
-- rule.
--
-- (19,2) is right for THIS column because it is typed by a human, never computed,
-- and never divided. The column does the single correct rounding and there is no
-- second CHECK to keep in step. Per mig 209 — which exists only to undo a
-- double-round introduced by a well-meaning round(x,4) three migrations earlier —
-- round NOWHERE else: not in a function, not in the repository.
--
-- ===== WHAT THIS TABLE CANNOT ENFORCE, AND WHERE THE REAL GATE IS =====
--
-- Two rules a reader will assume are enforced here, and are not:
--
-- 1. THAT THE TARGET IS A CATEGORY AT ALL. The composite FK points at
--    accounts(id, ledger_id), and categories ARE rows in accounts — so nothing
--    stops a target row naming a bank account. Enforcing it in SQL would need a
--    unique index on (id, ledger_id, category_kind) to compose against, and
--    ADR-0032 makes a trigger a last resort. The repository is the gate.
--
-- 2. THAT A TARGET AND ITS ANCESTORS ARE MUTUALLY EXCLUSIVE (ADR-0099 D1a) — a
--    target may sit on a node OR on its descendants, never both, so that each
--    subtree has exactly one authoritative mark and the hero's roots-only ceiling
--    needs no special case. That is a RECURSIVE TREE PREDICATE: no CHECK and no
--    unique index can express it. It lives in API code per ADR-0032, refuses the
--    write with a business error naming the node that already holds a target, and
--    needs hooks on reparent and merge — both of which can manufacture a violation
--    out of two individually legal states.
--
-- ===== THE FIVE SNAPSHOT FUNCTIONS BELOW =====
--
-- A new ledger-scoped table is invisible to snapshots until five function bodies
-- name it, and each has to be re-declared WHOLE. The bodies here were extracted
-- from the live catalog with
--
--     SELECT pg_get_functiondef('fn_ledger_snapshot_payload'::regproc);
--
-- and NOT copied from the migration that introduced each one. That distinction has
-- already cost this repo once: mig 205 re-declared a body from a superseded copy
-- and silently reverted a scale fix from three migrations earlier, which is the
-- entire reason mig 209 exists. Current owners are not where you would guess —
-- fn_ledger_snapshot_payload last changed in 188, while the other four last
-- changed in 193, so 193 is NOT the latest owner of everything it introduced.
--
-- Two asymmetries worth knowing before editing these lists:
--
--   * part_names is compared as a SET by the test suite; insert_order is FK
--     ORDER. Putting budget_targets before 'accounts' in insert_order would pass
--     CI and then throw 23503 on any restore of a ledger that has targets.
--   * fn_ledger_snapshot_parts_payload (owner: 195) does NOT enumerate tables and
--     therefore needs no change. Touching it would be churn.
-- =============================================================================

BEGIN;

CREATE TABLE budget_targets (
    id            UUID           NOT NULL DEFAULT gen_random_uuid(),
    ledger_id     UUID           NOT NULL,
    category_id   UUID           NOT NULL,

    -- The month this target applies to, normalised to its first day. Storing a
    -- DATE rather than (year, month) keeps range queries and ordering trivial;
    -- the CHECK is what stops a mid-month date making two rows that are the
    -- same month to a human and different to the UNIQUE index.
    target_month  DATE           NOT NULL,

    amount        NUMERIC(19,2)  NOT NULL,

    created_at    TIMESTAMPTZ    NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ    NOT NULL DEFAULT now(),

    CONSTRAINT pk_budget_targets PRIMARY KEY (id),

    CONSTRAINT uq_budget_targets_category_month
        UNIQUE (ledger_id, category_id, target_month),

    CONSTRAINT ck_budget_targets_month_is_first_of_month
        CHECK (target_month = date_trunc('month', target_month)::date),

    -- A target of zero is meaningful ("I intend to spend nothing here"), so only
    -- negatives are refused: a budget to earn money in an expense category is not
    -- a thing the UI can render or the comparison can interpret.
    CONSTRAINT ck_budget_targets_amount_non_negative
        CHECK (amount >= 0),

    CONSTRAINT fk_budget_targets_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers(id) ON DELETE CASCADE,

    CONSTRAINT fk_budget_targets_category
        FOREIGN KEY (category_id, ledger_id)
        REFERENCES accounts(id, ledger_id) ON DELETE CASCADE
);

-- The budget screen reads one month for one ledger at a time; uq_ above already
-- serves (ledger_id, category_id, target_month) prefix lookups, so this covers the
-- month-first access the GET actually makes.
CREATE INDEX ix_budget_targets_ledger_month
    ON budget_targets (ledger_id, target_month);

-- ---------------------------------------------------------------------------
-- RLS, role-aware per mig 174 / ADR-0083 D2. A table added after 174 gets no
-- protection unless it declares the pair itself, and ALTER DEFAULT PRIVILEGES
-- already grants coffer_app write — so "no policy" would mean "no refusal".
--
-- Nothing in CI, preflight or the schema guards enumerates relrowsecurity, and
-- the budget integration tests build both contexts from the service role, which
-- is BYPASSRLS. So a missing policy here would be invisible to every gate.
-- mig 207 made exactly this mistake on two tables and it survived to mig 213.
-- ---------------------------------------------------------------------------
ALTER TABLE budget_targets ENABLE ROW LEVEL SECURITY;

CREATE POLICY budget_targets_read
    ON budget_targets FOR SELECT TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

CREATE POLICY budget_targets_write
    ON budget_targets FOR ALL TO coffer_app
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

-- ---------------------------------------------------------------------------
-- Snapshot wiring. See the header: these five bodies are the live definitions
-- with budget_targets added, re-declared whole.
-- ---------------------------------------------------------------------------

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
        'txn_header_overrides',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_overrides t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_overrides',                (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_overrides t WHERE t.ledger_id = p_ledger_id),
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
$function$
;

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
    INSERT INTO txn_header_overrides       SELECT * FROM jsonb_populate_recordset(NULL::txn_header_overrides,       v_payload->'txn_header_overrides');
    INSERT INTO txn_leg_overrides          SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_overrides,          v_payload->'txn_leg_overrides');
    INSERT INTO txn_header_tags            SELECT * FROM jsonb_populate_recordset(NULL::txn_header_tags,            v_payload->'txn_header_tags');

    PERFORM fn_recompute_balances_for_ledger(p_ledger_id);
END;
$function$
;

CREATE OR REPLACE FUNCTION public.fn_ledger_snapshot_part_names()
 RETURNS text[]
 LANGUAGE sql
 IMMUTABLE
AS $function$
    SELECT ARRAY[
        'accounts', 'securities', 'user_account_groups', 'account_external_ids',
        'security_prices', 'security_splits', 'holdings',
        'user_account_group_members', 'txn_headers', 'txn_legs', 'txn_leg_recon',
        'lots', 'realized_gains', 'txn_header_overrides', 'txn_leg_overrides',
        'tags', 'txn_header_tags', 'provider_security_mappings',
        'recurring_transactions', 'recurring_occurrence_exceptions', 'loan_terms',
        'budget_targets'
    ];
$function$
;

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
        'txn_header_overrides', 'txn_leg_overrides', 'txn_header_tags'
    ];
$function$
;

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
    DELETE FROM txn_leg_overrides          WHERE ledger_id = p_ledger_id;
    DELETE FROM txn_header_overrides       WHERE ledger_id = p_ledger_id;
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
$function$
;

COMMIT;
