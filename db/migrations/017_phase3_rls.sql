-- Phase 3 PR 3.8: Row-level security turn-on with role split (ADR-0020 Phase D).
--
-- Engages Postgres RLS as the database-level safety net under the
-- app-layer per-ledger gate that PRs 3.6–3.7 introduced. From this
-- migration onward, the API connects as `coffer_app` (no BYPASSRLS) and
-- every request sets `app.user_id` once via SET on the pooled
-- connection; the importer, sync worker, and migration runner connect
-- as `coffer_service` (BYPASSRLS) which sees every row across every
-- ledger.
--
-- The roles themselves are provisioned outside this file (operator
-- step or db/init/00-init-roles.sh in docker-compose). This file
-- guards against missing roles up front so a half-provisioned
-- environment fails loudly with a clear message instead of silently
-- granting privileges to roles that don't exist yet.

-- ---------------------------------------------------------------------------
-- 1) Guard: both roles must already exist (provisioned via the operator
--    init step). Fail loudly if either is missing.
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'coffer_service') THEN
        RAISE EXCEPTION 'Role coffer_service is missing. '
            'Provision both roles (db/init/00-init-roles.sh for docker-compose, '
            'or manually for other deployments) before running migrations. '
            'See docs/operations.md.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'coffer_app') THEN
        RAISE EXCEPTION 'Role coffer_app is missing. '
            'Provision both roles (db/init/00-init-roles.sh for docker-compose, '
            'or manually for other deployments) before running migrations. '
            'See docs/operations.md.';
    END IF;

    -- coffer_service must have BYPASSRLS or the importer + this migration
    -- runner can't function. coffer_app must NOT have BYPASSRLS or the
    -- whole role split is moot. Catch a misconfigured init script that
    -- creates the roles with the wrong attribute.
    IF NOT (SELECT rolbypassrls FROM pg_roles WHERE rolname = 'coffer_service') THEN
        RAISE EXCEPTION 'Role coffer_service must have BYPASSRLS. '
            'Fix the init script (db/init/00-init-roles.sh) and recreate the role.';
    END IF;
    IF (SELECT rolbypassrls FROM pg_roles WHERE rolname = 'coffer_app') THEN
        RAISE EXCEPTION 'Role coffer_app must NOT have BYPASSRLS. '
            'Fix the init script (db/init/00-init-roles.sh) and recreate the role.';
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 2) Schema + table access grants.
--
-- coffer_service inherits BYPASSRLS but still needs explicit table
-- privileges (BYPASSRLS only bypasses row filtering, not column-level
-- SELECT/INSERT/etc.). Granting CRUD on existing tables and arranging
-- for future tables to inherit the same via DEFAULT PRIVILEGES.
-- ---------------------------------------------------------------------------
GRANT USAGE ON SCHEMA public TO coffer_service, coffer_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public
    TO coffer_service, coffer_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public
    TO coffer_service, coffer_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public
    TO coffer_service, coffer_app;

-- Future tables created by the (superuser) migration runner inherit the
-- same grant set, so a forgotten GRANT in a later migration doesn't
-- silently lock coffer_app out of a new table.
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO coffer_service, coffer_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO coffer_service, coffer_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT EXECUTE ON FUNCTIONS TO coffer_service, coffer_app;

-- bootstrap_tokens is a global one-shot table (no per-user concept,
-- pre-auth handling). Reserved to the service role; coffer_app has no
-- legitimate read/write reason to touch it. The bootstrap-setup
-- ceremony runs as the service role per the pre-auth boundary.
REVOKE ALL ON TABLE bootstrap_tokens FROM coffer_app;

-- ---------------------------------------------------------------------------
-- 3) Views run with the querying user's permissions, not the owner's.
--
-- Without security_invoker = true, a view owned by the superuser would
-- bypass RLS on the underlying tables — querying `user_visible_ledgers`
-- as coffer_app would see every row instead of the user's grants. Both
-- views need this flag.
-- ---------------------------------------------------------------------------
ALTER VIEW user_visible_ledgers SET (security_invoker = true);
ALTER VIEW resolved_transactions SET (security_invoker = true);

-- ---------------------------------------------------------------------------
-- 4) Helper: current_app_user_id().
--
-- Centralizes the "read app.user_id from the session, cast to uuid,
-- return NULL if unset" pattern. Returns NULL when the GUC hasn't been
-- set yet (pre-auth or a bug where the middleware skipped SET) —
-- callers don't need to repeat the cast or the `, true` second arg.
-- Marked STABLE so the planner can cache the result within a query
-- evaluation.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION current_app_user_id() RETURNS UUID
LANGUAGE SQL STABLE
AS $$
    SELECT NULLIF(current_setting('app.user_id', true), '')::uuid;
$$;
GRANT EXECUTE ON FUNCTION current_app_user_id() TO coffer_app, coffer_service;

-- ---------------------------------------------------------------------------
-- 5) Identity + auth tables — SELECT-only policies for coffer_app.
--
-- Insert/update/delete on these tables happens through the auth
-- subsystem (CookieAuthHandler, SessionService, SetupEndpoints,
-- LoginEndpoints) which connects via the service role to bridge the
-- pre-auth window. Restricting coffer_app to SELECT here prevents two
-- classes of privilege escalation:
--   - A bug in user-facing endpoint code can't mint a session or
--     credential row even if it manages to construct one.
--   - A user with knowledge of another ledger's UUID can't grant
--     themselves access by inserting into user_ledger_grants directly.
--
-- The auth subsystem mediates every legitimate write to these tables,
-- so service-role mediation is the natural boundary.
-- ---------------------------------------------------------------------------

-- users: FOR ALL on the caller's own row. SELECTs return only that
-- row; UPDATEs are bounded to it (the only legitimate self-service
-- write today is users.last_opened_ledger_id from the auto-open
-- flow). INSERTs from coffer_app are effectively blocked by the
-- existing UNIQUE constraint on id (the caller would have to invent
-- a fresh uuid AND match their own current_app_user_id — impossible).
-- Cross-cutting writes (creating new users, disabling accounts) go
-- through the service role.
ALTER TABLE users ENABLE ROW LEVEL SECURITY;
CREATE POLICY users_self ON users FOR ALL TO coffer_app
    USING      (id = current_app_user_id())
    WITH CHECK (id = current_app_user_id());

-- user_ledger_grants: SELECT-only. v1 shows only the caller's own grant
-- rows; the "grants on shared ledgers" extension lands with the share-
-- management UI and gets its own predicate (or a SECURITY DEFINER view)
-- to avoid recursive policy evaluation.
ALTER TABLE user_ledger_grants ENABLE ROW LEVEL SECURITY;
CREATE POLICY user_ledger_grants_self ON user_ledger_grants FOR SELECT TO coffer_app
    USING (user_id = current_app_user_id());

ALTER TABLE auth_sessions ENABLE ROW LEVEL SECURITY;
CREATE POLICY auth_sessions_self ON auth_sessions FOR SELECT TO coffer_app
    USING (user_id = current_app_user_id());

ALTER TABLE webauthn_credentials ENABLE ROW LEVEL SECURITY;
CREATE POLICY webauthn_credentials_self ON webauthn_credentials FOR SELECT TO coffer_app
    USING (user_id = current_app_user_id());

ALTER TABLE recovery_codes ENABLE ROW LEVEL SECURITY;
CREATE POLICY recovery_codes_self ON recovery_codes FOR SELECT TO coffer_app
    USING (user_id = current_app_user_id());

ALTER TABLE webauthn_pending_challenges ENABLE ROW LEVEL SECURITY;
CREATE POLICY webauthn_pending_challenges_self ON webauthn_pending_challenges FOR SELECT TO coffer_app
    USING (user_id = current_app_user_id());

-- ledgers: SELECT-only. Ledger creation (POST /api/ledgers) is
-- service-mediated because the INSERT can't satisfy a "must already
-- have a grant" WITH CHECK predicate before the grant is itself
-- inserted in the same transaction.
ALTER TABLE ledgers ENABLE ROW LEVEL SECURITY;
CREATE POLICY ledgers_per_user ON ledgers FOR SELECT TO coffer_app
    USING (
        id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

-- ---------------------------------------------------------------------------
-- 6) Ledger-scoped anchor tables — FOR ALL with WITH CHECK == USING.
--
-- Each anchor row carries ledger_id directly (per ADR-0020 Rule 1).
-- The user IS the legitimate writer on these tables (creating
-- accounts, defining tags, adding rules, …), so coffer_app gets read +
-- write under the same predicate: the row's ledger_id must appear in
-- the current user's grant set. WITH CHECK enforces the same on
-- INSERT/UPDATE so a user can't create or move rows into a ledger
-- they don't have access to.
--
-- Omitting WITH CHECK would leave WITH CHECK defaulting to the USING
-- predicate, which is what we want — but spelling it explicitly here
-- makes the read/write symmetry obvious to a future reader.
-- ---------------------------------------------------------------------------

ALTER TABLE accounts ENABLE ROW LEVEL SECURITY;
CREATE POLICY accounts_per_user ON accounts FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

ALTER TABLE securities ENABLE ROW LEVEL SECURITY;
CREATE POLICY securities_per_user ON securities FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

ALTER TABLE feed_connections ENABLE ROW LEVEL SECURITY;
CREATE POLICY feed_connections_per_user ON feed_connections FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

ALTER TABLE tags ENABLE ROW LEVEL SECURITY;
CREATE POLICY tags_per_user ON tags FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

ALTER TABLE merge_rules ENABLE ROW LEVEL SECURITY;
CREATE POLICY merge_rules_per_user ON merge_rules FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

ALTER TABLE transaction_rules ENABLE ROW LEVEL SECURITY;
CREATE POLICY transaction_rules_per_user ON transaction_rules FOR ALL TO coffer_app
    USING (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    )
    WITH CHECK (
        ledger_id IN (
            SELECT ledger_id FROM user_ledger_grants
             WHERE user_id = current_app_user_id()
        )
    );

-- ---------------------------------------------------------------------------
-- 7) Derived tables — RLS policies that compose via FK chain.
--
-- These tables don't carry ledger_id directly. Their policy filters
-- against the parent anchor (accounts / transactions / securities /
-- feed_connections), which themselves are RLS-filtered above. Postgres
-- composes the predicates so a derived row is visible iff its parent
-- is visible iff the parent's ledger is in the user's grant set.
-- ---------------------------------------------------------------------------

ALTER TABLE transactions ENABLE ROW LEVEL SECURITY;
CREATE POLICY transactions_per_user ON transactions FOR ALL TO coffer_app
    USING      (account_id IN (SELECT id FROM accounts))
    WITH CHECK (account_id IN (SELECT id FROM accounts));

ALTER TABLE transaction_overrides ENABLE ROW LEVEL SECURITY;
CREATE POLICY transaction_overrides_per_user ON transaction_overrides FOR ALL TO coffer_app
    USING      (transaction_id IN (SELECT id FROM transactions))
    WITH CHECK (transaction_id IN (SELECT id FROM transactions));

-- (Tables `splits` and `inv_txn_securities` were dropped in
-- migration 011 / ADR-0019's symmetric-posting model — splits and
-- investment-side rows are now carried by paired transactions rows.
-- No RLS policies needed.)

ALTER TABLE holdings ENABLE ROW LEVEL SECURITY;
CREATE POLICY holdings_per_user ON holdings FOR ALL TO coffer_app
    USING      (account_id IN (SELECT id FROM accounts))
    WITH CHECK (account_id IN (SELECT id FROM accounts));

ALTER TABLE lots ENABLE ROW LEVEL SECURITY;
CREATE POLICY lots_per_user ON lots FOR ALL TO coffer_app
    USING      (transaction_id IN (SELECT id FROM transactions))
    WITH CHECK (transaction_id IN (SELECT id FROM transactions));

ALTER TABLE pending_transactions ENABLE ROW LEVEL SECURITY;
CREATE POLICY pending_transactions_per_user ON pending_transactions FOR ALL TO coffer_app
    USING      (account_id IN (SELECT id FROM accounts))
    WITH CHECK (account_id IN (SELECT id FROM accounts));

ALTER TABLE recurring_transactions ENABLE ROW LEVEL SECURITY;
CREATE POLICY recurring_transactions_per_user ON recurring_transactions FOR ALL TO coffer_app
    USING      (source_account_id IN (SELECT id FROM accounts))
    WITH CHECK (source_account_id IN (SELECT id FROM accounts));

ALTER TABLE security_prices ENABLE ROW LEVEL SECURITY;
CREATE POLICY security_prices_per_user ON security_prices FOR ALL TO coffer_app
    USING      (security_id IN (SELECT id FROM securities))
    WITH CHECK (security_id IN (SELECT id FROM securities));

ALTER TABLE transaction_tags ENABLE ROW LEVEL SECURITY;
CREATE POLICY transaction_tags_per_user ON transaction_tags FOR ALL TO coffer_app
    USING      (transaction_id IN (SELECT id FROM transactions))
    WITH CHECK (transaction_id IN (SELECT id FROM transactions));

ALTER TABLE merge_candidates ENABLE ROW LEVEL SECURITY;
CREATE POLICY merge_candidates_per_user ON merge_candidates FOR ALL TO coffer_app
    USING      (incoming_txn_id IN (SELECT id FROM transactions))
    WITH CHECK (incoming_txn_id IN (SELECT id FROM transactions));

-- sync_runs.feed_connection_id is nullable (a sync run can be detached
-- from its feed connection on cleanup). The service role drives sync
-- writes; coffer_app sees only rows tied to a visible feed connection.
ALTER TABLE sync_runs ENABLE ROW LEVEL SECURITY;
CREATE POLICY sync_runs_per_user ON sync_runs FOR ALL TO coffer_app
    USING      (feed_connection_id IS NOT NULL
                AND feed_connection_id IN (SELECT id FROM feed_connections))
    WITH CHECK (feed_connection_id IS NOT NULL
                AND feed_connection_id IN (SELECT id FROM feed_connections));

-- ---------------------------------------------------------------------------
-- 8) bootstrap_tokens: not enabling RLS. Reserved to service role via
--    the REVOKE above. Documented here so a future migration auditor
--    sees the deliberate exclusion alongside the rest.
-- ---------------------------------------------------------------------------
-- (intentionally no policy)
