-- =============================================================================
-- 127 — loan_terms (amortization parameters) + accounts.opened_on (ADR-0050)
-- (renumbered from 126: a parallel branch independently used 126 for the
--  recurring-transactions source-account catchup; both were cut at main=125.)
-- =============================================================================
--
-- A loan account (account_type='loan') carries amortization parameters that
-- Moneydance keeps on the account, not the reminder: original principal, annual
-- rate, term, escrow, the interest/escrow target accounts, and whether the
-- payment is computed (amortized) or a fixed specified value. loan_terms holds
-- them 1:1 with the loan account so a later slice can compute the
-- principal/interest/escrow split per occurrence (ADR-0050 D1–D7).
--
-- Ownership (ADR-0050 D10): MD import SEEDS this row once; Coffer owns it after.
-- The importer upserts with ON CONFLICT (account_id) DO NOTHING.
--
-- accounts.opened_on: the account's "Start Date" (MD has it for every account
-- type). Nullable; seeded from MD's creation date, editable in Coffer later.
--
-- FKs (mig-049/072 ledger-coherence pattern — accounts carries the (id,
-- ledger_id) unique):
--   * (account_id, ledger_id) -> accounts(id, ledger_id) ON DELETE CASCADE
--     (the terms are meaningless once the loan account is gone).
--   * ledger_id -> ledgers(id) ON DELETE CASCADE.
--   * (interest_account_id, ledger_id) / (escrow_account_id, ledger_id) ->
--     accounts(id, ledger_id) ON DELETE RESTRICT. RESTRICT (not SET NULL)
--     because the composite includes the NOT-NULL ledger_id, which SET NULL
--     would violate; a loan referencing an account simply pins it.
--
-- RLS: same flattened per-ledger policy as migrations 071/072/075/125.
-- =============================================================================

CREATE TABLE loan_terms (
    account_id            UUID PRIMARY KEY,
    ledger_id             UUID NOT NULL,
    original_principal    NUMERIC(20, 4) NOT NULL,
    annual_interest_rate  NUMERIC(9, 4)  NOT NULL,   -- percent, e.g. 3.6500
    points                NUMERIC(9, 4)  NOT NULL DEFAULT 0,
    payment_count         INTEGER        NOT NULL,
    payments_per_year     INTEGER        NOT NULL,
    first_payment_date    DATE           NULL,
    escrow_amount         NUMERIC(20, 4) NOT NULL DEFAULT 0,
    interest_account_id   UUID           NULL,
    escrow_account_id     UUID           NULL,
    payment_is_computed   BOOLEAN        NOT NULL DEFAULT TRUE,
    fixed_payment         NUMERIC(20, 4) NULL,        -- used when NOT computed
    created_at            TIMESTAMPTZ    NOT NULL DEFAULT now(),

    CONSTRAINT fk_loan_terms_account
        FOREIGN KEY (account_id, ledger_id)
        REFERENCES accounts (id, ledger_id) ON DELETE CASCADE,
    CONSTRAINT fk_loan_terms_ledger
        FOREIGN KEY (ledger_id) REFERENCES ledgers (id) ON DELETE CASCADE,
    CONSTRAINT fk_loan_terms_interest_account
        FOREIGN KEY (interest_account_id, ledger_id)
        REFERENCES accounts (id, ledger_id) ON DELETE RESTRICT,
    CONSTRAINT fk_loan_terms_escrow_account
        FOREIGN KEY (escrow_account_id, ledger_id)
        REFERENCES accounts (id, ledger_id) ON DELETE RESTRICT,

    CONSTRAINT ck_loan_terms_rate_nonneg       CHECK (annual_interest_rate >= 0),
    CONSTRAINT ck_loan_terms_principal_pos     CHECK (original_principal > 0),
    CONSTRAINT ck_loan_terms_payment_count_pos CHECK (payment_count > 0),
    CONSTRAINT ck_loan_terms_ppy_pos           CHECK (payments_per_year > 0),
    CONSTRAINT ck_loan_terms_points_nonneg     CHECK (points >= 0)
);

COMMENT ON TABLE loan_terms IS
    'ADR-0050: amortization parameters 1:1 with a loan account. MD seeds it '
    'once (ON CONFLICT DO NOTHING); Coffer owns it thereafter (D10).';

-- -----------------------------------------------------------------------------
-- accounts.opened_on — the account "Start Date" for every type (ADR-0050).
-- -----------------------------------------------------------------------------
ALTER TABLE accounts ADD COLUMN opened_on DATE NULL;

-- -----------------------------------------------------------------------------
-- RLS — same per-ledger flattened policy as migrations 071/072/075/125.
-- -----------------------------------------------------------------------------
ALTER TABLE loan_terms ENABLE ROW LEVEL SECURITY;
ALTER TABLE loan_terms FORCE  ROW LEVEL SECURITY;

DROP POLICY IF EXISTS loan_terms_per_user ON loan_terms;
CREATE POLICY loan_terms_per_user ON loan_terms
    FOR ALL
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
        WHERE ulg.user_id = current_app_user_id()));

GRANT SELECT, INSERT, UPDATE, DELETE ON loan_terms TO coffer_app;
GRANT ALL ON loan_terms TO coffer_service;

-- =============================================================================
-- Snapshot coverage (the mig 111 -> 112 lesson, ADR-0037) — add loan_terms to
-- both snapshot functions. Reproduced verbatim from mig 125 with ONLY the
-- loan_terms line/block added. loan_terms references accounts, so on DELETE it
-- goes BEFORE accounts (reverse-FK) and on INSERT AFTER accounts (forward-FK).
-- =============================================================================

CREATE OR REPLACE FUNCTION fn_ledger_snapshot_payload(p_ledger_id uuid)
RETURNS jsonb
LANGUAGE plpgsql
STABLE
AS $$
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
        'lots',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM lots t WHERE t.ledger_id = p_ledger_id),
        'txn_header_overrides',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_overrides t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_overrides',                (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_overrides t WHERE t.ledger_id = p_ledger_id),
        'tags',                             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM tags t WHERE t.ledger_id = p_ledger_id),
        'txn_header_tags',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_tags t WHERE t.ledger_id = p_ledger_id),
        'provider_security_mappings',       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM provider_security_mappings t WHERE t.ledger_id = p_ledger_id),
        'recurring_transactions',           (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_transactions t WHERE t.ledger_id = p_ledger_id),
        'recurring_occurrence_exceptions',  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_occurrence_exceptions t WHERE t.ledger_id = p_ledger_id),
        'loan_terms',                       (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM loan_terms t WHERE t.ledger_id = p_ledger_id)
    ) INTO v_result;
    RETURN v_result;
END;
$$;

CREATE OR REPLACE FUNCTION fn_ledger_snapshot_restore(
    p_ledger_id uuid,
    p_payload   text
)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_account_id uuid;
    v_payload    jsonb := p_payload::jsonb;
BEGIN
    -- ----- 1. Delete existing rows in reverse-FK order ------------------
    -- loan_terms references accounts; must go before accounts.
    DELETE FROM loan_terms                 WHERE ledger_id = p_ledger_id;
    -- recurring_occurrence_exceptions references recurring_transactions; go first.
    DELETE FROM recurring_occurrence_exceptions WHERE ledger_id = p_ledger_id;
    -- recurring_transactions references accounts; must go before accounts.
    DELETE FROM recurring_transactions     WHERE ledger_id = p_ledger_id;
    -- security_splits references securities; must go before securities.
    DELETE FROM security_splits            WHERE ledger_id = p_ledger_id;
    -- Children of txn_legs first.
    DELETE FROM lots                       WHERE ledger_id = p_ledger_id;
    -- Override layers + tag joins.
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

    -- ----- 2. Insert rows from the payload (forward-FK order) -----------
    -- Roots first.
    INSERT INTO accounts                   SELECT * FROM jsonb_populate_recordset(NULL::accounts,                   v_payload->'accounts');
    -- loan_terms references accounts (incl. interest/escrow targets) — after accounts.
    INSERT INTO loan_terms                 SELECT * FROM jsonb_populate_recordset(NULL::loan_terms,                 v_payload->'loan_terms');
    INSERT INTO securities                 SELECT * FROM jsonb_populate_recordset(NULL::securities,                 v_payload->'securities');
    INSERT INTO tags                       SELECT * FROM jsonb_populate_recordset(NULL::tags,                       v_payload->'tags');
    -- Children of roots.
    INSERT INTO account_external_ids       SELECT * FROM jsonb_populate_recordset(NULL::account_external_ids,       v_payload->'account_external_ids');
    INSERT INTO security_prices            SELECT * FROM jsonb_populate_recordset(NULL::security_prices,            v_payload->'security_prices');
    INSERT INTO security_splits            SELECT * FROM jsonb_populate_recordset(NULL::security_splits,            v_payload->'security_splits');
    INSERT INTO holdings                   SELECT * FROM jsonb_populate_recordset(NULL::holdings,                   v_payload->'holdings');
    INSERT INTO user_account_groups        SELECT * FROM jsonb_populate_recordset(NULL::user_account_groups,        v_payload->'user_account_groups');
    INSERT INTO user_account_group_members SELECT * FROM jsonb_populate_recordset(NULL::user_account_group_members, v_payload->'user_account_group_members');
    INSERT INTO provider_security_mappings SELECT * FROM jsonb_populate_recordset(NULL::provider_security_mappings, v_payload->'provider_security_mappings');
    INSERT INTO recurring_transactions     SELECT * FROM jsonb_populate_recordset(NULL::recurring_transactions,     v_payload->'recurring_transactions');
    -- Transaction graph.
    INSERT INTO txn_headers                SELECT * FROM jsonb_populate_recordset(NULL::txn_headers,                v_payload->'txn_headers');
    INSERT INTO txn_legs                   SELECT * FROM jsonb_populate_recordset(NULL::txn_legs,                   v_payload->'txn_legs');
    INSERT INTO lots                       SELECT * FROM jsonb_populate_recordset(NULL::lots,                       v_payload->'lots');
    -- recurring_occurrence_exceptions references recurring_transactions — after it.
    INSERT INTO recurring_occurrence_exceptions SELECT * FROM jsonb_populate_recordset(NULL::recurring_occurrence_exceptions, v_payload->'recurring_occurrence_exceptions');
    -- Override layers last.
    INSERT INTO txn_header_overrides       SELECT * FROM jsonb_populate_recordset(NULL::txn_header_overrides,       v_payload->'txn_header_overrides');
    INSERT INTO txn_leg_overrides          SELECT * FROM jsonb_populate_recordset(NULL::txn_leg_overrides,          v_payload->'txn_leg_overrides');
    INSERT INTO txn_header_tags            SELECT * FROM jsonb_populate_recordset(NULL::txn_header_tags,            v_payload->'txn_header_tags');

    -- ----- 3. Rebuild materialised balances per account -----------------
    FOR v_account_id IN
        SELECT a.id FROM accounts a WHERE a.ledger_id = p_ledger_id
    LOOP
        PERFORM fn_recompute_balances_for_account(v_account_id, '0001-01-01'::timestamptz);
    END LOOP;
END;
$$;
