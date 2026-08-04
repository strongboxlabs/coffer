-- =============================================================================
-- 111 — ledger_snapshots table (ADR-0037)
-- =============================================================================
--
-- Server-side capped snapshots of the user-curated ledger graph. The
-- pre-risk safety net half of ADR-0037's two-track recovery design.
-- Weekly auto-snap (created by the system user) + user-initiated
-- manual snaps; cap of 5 per ledger enforced in repository logic
-- (the eviction rule — auto-evicts-auto-first, manual stays until
-- explicit delete — is non-trivial enough that a DB constraint
-- would be uglier than the LINQ).
--
-- `content` is gzip-compressed JSON of the in-scope tables only
-- (per ADR §"Scope (in)"); operational state (feed_connections,
-- sync_runs, sessions) is excluded. The materialised balance table
-- `txn_header_account_balances` is also excluded — re-derived on
-- restore via fn_recompute_balances_for_account to avoid the
-- materialisation drifting from txn_legs.
--
-- `content_size_uncompressed` is stored for SPA display
-- ("47 MB before compression" on the snapshots list) without
-- requiring the API to decompress on every list call.
--
-- Auto-snaps carry created_by_user_id = the system user
-- (00000000-0000-0000-0000-000000000001 from the bootstrap
-- migrations); manual snaps carry the acting user.

CREATE TABLE ledger_snapshots (
    id                          uuid        PRIMARY KEY,
    ledger_id                   uuid        NOT NULL
        REFERENCES ledgers(id) ON DELETE CASCADE,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    created_by_user_id          uuid        NOT NULL
        REFERENCES users(id) ON DELETE RESTRICT,
    kind                        text        NOT NULL
        CHECK (kind IN ('auto', 'manual')),
    description                 text,
    schema_version              text        NOT NULL,
    content                     bytea       NOT NULL,
    content_size_uncompressed   integer     NOT NULL
        CHECK (content_size_uncompressed >= 0)
);

-- Snapshots-list query (newest-first per ledger) is the hot path.
CREATE INDEX idx_ledger_snapshots_ledger_created
    ON ledger_snapshots (ledger_id, created_at DESC);

-- Eviction rule needs to find "the oldest auto-snap for this
-- ledger" — supporting index keeps that O(log n).
CREATE INDEX idx_ledger_snapshots_ledger_kind_created
    ON ledger_snapshots (ledger_id, kind, created_at);

COMMENT ON TABLE ledger_snapshots IS
    'ADR-0037: pre-risk safety-net snapshots of the user-curated ledger graph. '
    'Capped at 5 per ledger; auto-evicts-auto-first per LedgerSnapshotsRepository.';

COMMENT ON COLUMN ledger_snapshots.content IS
    'gzip-compressed JSON serialising the in-scope tables (per ADR-0037 §Scope). '
    'Decompress + deserialise via LedgerSnapshotSerializer to restore.';

COMMENT ON COLUMN ledger_snapshots.schema_version IS
    'DB schema version (from __schema_migrations) at snapshot creation time. '
    'Restore refuses when this does not match the live DB.';

-- ============================================================================
-- fn_ledger_snapshot_payload(p_ledger_id uuid) RETURNS jsonb
-- ============================================================================
--
-- Builds the snapshot payload's `tables` object for one ledger by
-- walking the in-scope tables per ADR-0037 §Scope. Returns:
--
--   { "accounts": [...], "securities": [...], ..., "provider_security_mappings": [...] }
--
-- The C# side wraps this in the envelope (snapshotFormat, schemaVersion,
-- ledgerId, createdAt), gzips, and persists. Order is irrelevant for
-- the payload itself (the restorer enforces FK-safe order at insert
-- time); we list tables here in roughly create-before-reference order
-- for readability + grep-ability.
--
-- "In-scope" matches LedgerSnapshotPayload.InScopeTables exactly. If you
-- add a table to one list, add it to both — they're checked against
-- each other by the round-trip integration test.
--
-- Out: feed_connections + their ciphertexts, sync_runs and friends,
-- auth_sessions, schema_migrations, txn_header_account_balances
-- (re-derived on restore).
--
-- Bound to EF as the `ledger_snapshot_payload` TVF below; C# calls
-- via LINQ per the no-raw-sql-in-api rule (memory: complex SQL lives
-- in Postgres functions / views, bound via HasDbFunction).
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
        'holdings',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM holdings t WHERE t.ledger_id = p_ledger_id),
        'user_account_group_members',  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM user_account_group_members t WHERE t.ledger_id = p_ledger_id),
        'txn_headers',                 (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_headers t WHERE t.ledger_id = p_ledger_id),
        'txn_legs',                    (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_legs t WHERE t.ledger_id = p_ledger_id),
        'lots',                        (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM lots t WHERE t.ledger_id = p_ledger_id),
        'txn_header_overrides',        (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_overrides t WHERE t.ledger_id = p_ledger_id),
        'txn_leg_overrides',           (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_leg_overrides t WHERE t.ledger_id = p_ledger_id),
        'tags',                        (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM tags t WHERE t.ledger_id = p_ledger_id),
        'txn_header_tags',             (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM txn_header_tags t WHERE t.ledger_id = p_ledger_id),
        'provider_security_mappings',  (SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb) FROM provider_security_mappings t WHERE t.ledger_id = p_ledger_id)
    ) INTO v_result;
    RETURN v_result;
END;
$$;

COMMENT ON FUNCTION fn_ledger_snapshot_payload(uuid) IS
    'ADR-0037: build the snapshot tables-payload for one ledger. Walks the '
    'in-scope tables (per §Scope) and returns one jsonb keyed by table name. '
    'C# side wraps in the envelope + gzips before persisting.';

-- ============================================================================
-- fn_ledger_snapshot_restore(p_ledger_id uuid, p_payload jsonb) RETURNS void
-- ============================================================================
--
-- Replaces one ledger's in-scope rows with the contents of a payload.
-- ENTIRE operation runs under the caller's transaction (this function
-- is plpgsql, called via EF inside a `using var tx = await
-- _db.Database.BeginTransactionAsync(...)`). If any step throws the
-- caller's rollback unwinds the whole restore.
--
-- Steps:
--   1. Delete rows in REVERSE-FK order so children don't dangle.
--   2. Insert rows from the payload in FORWARD-FK order via
--      jsonb_populate_recordset, which maps row objects'
--      column-name keys to the target row type.
--   3. Recompute balances for every account that ended up in
--      the restored set (full-walk; the prior balance rows
--      were swept by the txn_legs delete + the
--      BalanceRecomputeInterceptor on the C# side OR by the
--      direct fn_recompute_balances_for_account call here).
--
-- Caller is expected to have validated p_payload's schemaVersion
-- matches the live DB BEFORE invoking. This function does not
-- re-check the envelope.
-- Parameter is TEXT (cast to jsonb internally) — EF Core 10 + Npgsql
-- send string parameters as TEXT even with HasParameter().HasStoreType
-- ("jsonb"). Mirrors the mig 070 fix to insert_investment_legs.
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
    -- The materialised balance table — wiped so the recompute below
    -- rebuilds from scratch. (Cascade from accounts already did this
    -- in practice, but the explicit DELETE makes the contract clear.)
    DELETE FROM txn_header_account_balances WHERE ledger_id = p_ledger_id;

    -- ----- 2. Insert rows from the payload (forward-FK order) -----------
    -- Roots first.
    INSERT INTO accounts                   SELECT * FROM jsonb_populate_recordset(NULL::accounts,                   v_payload->'accounts');
    INSERT INTO securities                 SELECT * FROM jsonb_populate_recordset(NULL::securities,                 v_payload->'securities');
    INSERT INTO tags                       SELECT * FROM jsonb_populate_recordset(NULL::tags,                       v_payload->'tags');
    -- Children of roots.
    INSERT INTO account_external_ids       SELECT * FROM jsonb_populate_recordset(NULL::account_external_ids,       v_payload->'account_external_ids');
    INSERT INTO security_prices            SELECT * FROM jsonb_populate_recordset(NULL::security_prices,            v_payload->'security_prices');
    INSERT INTO holdings                   SELECT * FROM jsonb_populate_recordset(NULL::holdings,                   v_payload->'holdings');
    INSERT INTO user_account_groups        SELECT * FROM jsonb_populate_recordset(NULL::user_account_groups,        v_payload->'user_account_groups');
    INSERT INTO user_account_group_members SELECT * FROM jsonb_populate_recordset(NULL::user_account_group_members, v_payload->'user_account_group_members');
    INSERT INTO provider_security_mappings SELECT * FROM jsonb_populate_recordset(NULL::provider_security_mappings, v_payload->'provider_security_mappings');
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

COMMENT ON FUNCTION fn_ledger_snapshot_restore(uuid, text) IS
    'ADR-0037: restore one ledger from a snapshot payload. Caller must wrap '
    'in a transaction + validate schemaVersion match. Wipes in-scope tables '
    'for the ledger, re-inserts from payload, recomputes balances per '
    'account. fn_recompute_balances_for_account is called per account; '
    'no separate trigger fires.';

-- ============================================================================
-- TVF wrappers for EF binding (no-raw-sql-in-api rule)
-- ============================================================================
--
-- C# repositories invoke Postgres functions through HasDbFunction-
-- bound LINQ; that requires TABLE-returning signatures. The
-- fn_-prefixed functions above remain the implementation; these
-- wrappers expose them via TABLE shapes EF Core's expression
-- translator can map.

CREATE OR REPLACE FUNCTION ledger_snapshot_payload(p_ledger_id uuid)
RETURNS TABLE(payload jsonb)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY SELECT fn_ledger_snapshot_payload(p_ledger_id);
END;
$$;

COMMENT ON FUNCTION ledger_snapshot_payload(uuid) IS
    'TVF wrapper around fn_ledger_snapshot_payload. EF binds this via '
    'HasDbFunction; the LedgerSnapshotsRepository invokes it via '
    'LINQ to satisfy the no-raw-sql-in-api rule (ADR-0037).';

CREATE OR REPLACE FUNCTION ledger_snapshot_restore(
    p_ledger_id uuid,
    p_payload   text
)
RETURNS TABLE(ledger_id uuid)
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM fn_ledger_snapshot_restore(p_ledger_id, p_payload);
    RETURN QUERY SELECT p_ledger_id;
END;
$$;

COMMENT ON FUNCTION ledger_snapshot_restore(uuid, text) IS
    'TVF wrapper around fn_ledger_snapshot_restore. EF binds this via '
    'HasDbFunction; the LedgerSnapshotsRepository invokes it inside an '
    'explicit transaction so the caller controls rollback on payload-shape '
    'errors that surface from the underlying jsonb_populate_recordset calls.';

-- Postgres-side execution privileges. Mirrors the GRANT pattern used
-- by recompute_balances_for_account in mig 102.
GRANT EXECUTE ON FUNCTION fn_ledger_snapshot_payload(uuid) TO coffer_app;
GRANT EXECUTE ON FUNCTION fn_ledger_snapshot_restore(uuid, text) TO coffer_app;
GRANT EXECUTE ON FUNCTION ledger_snapshot_payload(uuid) TO coffer_app;
GRANT EXECUTE ON FUNCTION ledger_snapshot_restore(uuid, text) TO coffer_app;

