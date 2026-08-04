-- =============================================================================
-- 112 — ledger snapshot scope: add recurring_transactions + security_splits
-- =============================================================================
--
-- Mig 111 shipped the snapshot functions with an in-scope list that
-- missed two per-ledger tables. The failure surfaced as a 23503 FK
-- violation on `recurring_transactions_source_account_id_fkey` the
-- first time the user tried to restore — the wipe-delete on
-- accounts can't proceed while recurring rows still reference them.
--
-- Two tables to fold into the in-scope graph:
--
--   * recurring_transactions — user-curated payment schedules (ADR-0010).
--     References accounts (source + optional target). Per-ledger.
--     User data; restore-it.
--
--   * security_splits — corporate-action records driving cost-basis
--     adjustments (B0.7). References securities. Per-ledger. User
--     data; restore-it.
--
-- Both run through the same delete-then-insert pattern as the
-- existing tables, in FK-safe order.
--
-- Memory: `LedgerSnapshotPayload.InScopeTables` in C# also gains
-- these two entries — keep the two lists aligned.
--
-- `user_ledger_grants` is also per-ledger but explicitly OUT of
-- scope (it's identity / permission data per ADR-0037's
-- "identity stays out" rule; the new install's setup user takes
-- ownership of restored data, not the snapshot's prior grants).

CREATE OR REPLACE FUNCTION fn_ledger_snapshot_payload(p_ledger_id uuid)
RETURNS jsonb
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_result jsonb;
BEGIN
    SELECT jsonb_build_object(
        'accounts',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM accounts t WHERE t.ledger_id = p_ledger_id),
        'securities',                  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM securities t WHERE t.ledger_id = p_ledger_id),
        'user_account_groups',         (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM user_account_groups t WHERE t.ledger_id = p_ledger_id),
        'account_external_ids',        (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM account_external_ids t WHERE t.ledger_id = p_ledger_id),
        'security_prices',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_prices t WHERE t.ledger_id = p_ledger_id),
        'security_splits',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM security_splits t WHERE t.ledger_id = p_ledger_id),
        'holdings',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM holdings t WHERE t.ledger_id = p_ledger_id),
        'user_account_group_members',  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM user_account_group_members t WHERE t.ledger_id = p_ledger_id),
        'txn_headers',                 (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_headers t WHERE t.ledger_id = p_ledger_id),
        'txn_legs',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_legs t WHERE t.ledger_id = p_ledger_id),
        'lots',                        (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM lots t WHERE t.ledger_id = p_ledger_id),
        'txn_header_overrides',        (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_overrides t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_overrides',           (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_overrides t WHERE t.ledger_id = p_ledger_id),
        'tags',                        (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM tags t WHERE t.ledger_id = p_ledger_id),
        'txn_header_tags',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_tags t WHERE t.ledger_id = p_ledger_id),
        'provider_security_mappings',  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM provider_security_mappings t WHERE t.ledger_id = p_ledger_id),
        'recurring_transactions',      (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM recurring_transactions t WHERE t.ledger_id = p_ledger_id)
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
