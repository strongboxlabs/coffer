-- 254 — Every privilege coffer_app holds is granted on purpose, and every write a policy
-- lets through is the writer's own row or theirs by role (FU-086)
--
-- Migration 017 set DEFAULT PRIVILEGES so that every table created later handed
-- coffer_app SELECT, INSERT, UPDATE and DELETE on its own. A table that then shipped
-- without row-level security was open to every request, and that happened at least four
-- times (072, 175/231, 207, 243). The API's own authorization hid each one. Below:
--
--   1. The default grant goes. A new table is now closed to coffer_app until its
--      migration grants exactly what its policies serve; RlsCatalogTests fails until
--      the two agree.
--   2. Privileges no policy can ever use go too. With RLS on they did nothing; with RLS
--      off by mistake they were the whole door.
--   3. Two tables coffer_app never touches become service-only: invites (InvitesRepository
--      uses the service role for every operation) and ledger_snapshot_parts (create and
--      restore run as the service role; a snapshot's delete reaches its parts through the
--      foreign key's cascade, which runs as the table owner). A part holds every member's
--      private rows of the ledger: account groups, report pins, private saved reports.
--   4. Writes to ledger data are owner/editor: txn_leg_tags (created after 174), the
--      operation log, schedules and snapshots. 174 left the last three out because "the
--      worker writes them as the service role"; manual syncs, file imports, quote refreshes
--      and snapshot deletes write them as coffer_app, behind the API's write gate only.
--   5. Rows that belong to one user are writable by that user alone: another user's
--      account group's members, and another user's shared saved report, which any member
--      could delete (DELETE is decided by USING, and USING admitted every shared report).
--
-- Deterministic: no data is read or written, only the catalog.

-- ---------------------------------------------------------------------------
-- 1) No default grant. Applies to the objects the role running migrations creates,
--    which is the role that set it in 017.
-- ---------------------------------------------------------------------------
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM coffer_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM coffer_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM coffer_app;

-- ---------------------------------------------------------------------------
-- 2) Privileges no policy can use. Each of these tables is read-only to coffer_app by
--    policy; every write to them runs as the service role.
-- ---------------------------------------------------------------------------
REVOKE INSERT, UPDATE, DELETE ON
    auth_sessions,
    ledger_consistency_state,
    ledger_events,
    ledgers,
    recovery_codes,
    system_events,
    user_ledger_grants,
    webauthn_credentials,
    webauthn_pending_challenges
FROM coffer_app;

-- Views are read through; their base tables' policies apply (security_invoker).
REVOKE INSERT, UPDATE, DELETE ON
    account_current_balances,
    live_txn_headers,
    resolved_transactions,
    template_txn_headers,
    user_visible_ledgers
FROM coffer_app;

-- DbUp's journal, created by the migration runner before migration 001 and so given the
-- default grant like everything else. Deleting a row from it re-runs that migration.
-- Reading it stays: the version endpoint and the snapshot paths stamp and compare the
-- schema version from it, and a migration's name says nothing about anyone's data.
-- Absent where migrations are applied by psql (CI's schema job).
DO $$
BEGIN
    IF to_regclass('public."__schema_migrations"') IS NOT NULL THEN
        REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
            ON TABLE public."__schema_migrations" FROM coffer_app;
    END IF;
END $$;

-- ---------------------------------------------------------------------------
-- 3) Service-only: no coffer_app policy, no coffer_app privilege.
-- ---------------------------------------------------------------------------
DROP POLICY invites_read  ON invites;
DROP POLICY invites_write ON invites;
REVOKE ALL ON invites FROM coffer_app;
ALTER TABLE invites FORCE ROW LEVEL SECURITY;

DROP POLICY ledger_snapshot_parts_read  ON ledger_snapshot_parts;
DROP POLICY ledger_snapshot_parts_write ON ledger_snapshot_parts;
REVOKE ALL ON ledger_snapshot_parts FROM coffer_app;
ALTER TABLE ledger_snapshot_parts FORCE ROW LEVEL SECURITY;

-- ---------------------------------------------------------------------------
-- 4) Members read; owners and editors write. The 174 pattern: two permissive policies,
--    OR-combined, so SELECT passes through _read and every write needs _write.
-- ---------------------------------------------------------------------------
DROP POLICY txn_leg_tags_per_user ON txn_leg_tags;
CREATE POLICY txn_leg_tags_read ON txn_leg_tags FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()));
CREATE POLICY txn_leg_tags_write ON txn_leg_tags FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));

DROP POLICY ledger_operations_per_user ON ledger_operations;
CREATE POLICY ledger_operations_read ON ledger_operations FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()));
CREATE POLICY ledger_operations_write ON ledger_operations FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));

DROP POLICY ledger_operation_errors_per_user ON ledger_operation_errors;
CREATE POLICY ledger_operation_errors_read ON ledger_operation_errors FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()));
CREATE POLICY ledger_operation_errors_write ON ledger_operation_errors FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));

DROP POLICY ledger_operation_promotions_per_user ON ledger_operation_promotions;
CREATE POLICY ledger_operation_promotions_read ON ledger_operation_promotions FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()));
CREATE POLICY ledger_operation_promotions_write ON ledger_operation_promotions FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));

DROP POLICY scheduled_jobs_per_ledger ON scheduled_jobs;
CREATE POLICY scheduled_jobs_read ON scheduled_jobs FOR SELECT TO coffer_app
    USING (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                          WHERE ulg.user_id = current_app_user_id()));
CREATE POLICY scheduled_jobs_write ON scheduled_jobs FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));

DROP POLICY ledger_snapshots_write ON ledger_snapshots;
CREATE POLICY ledger_snapshots_write ON ledger_snapshots FOR ALL TO coffer_app
    USING      (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')))
    WITH CHECK (ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                               WHERE ulg.user_id = current_app_user_id()
                                 AND ulg.role IN ('owner', 'editor')));

-- ---------------------------------------------------------------------------
-- 5) One user's rows, writable by that user alone.
-- ---------------------------------------------------------------------------

-- 072 flattened this to "any member of the ledger", dropping 033's check that the group
-- is yours: any member could add to or empty another member's group.
DROP POLICY user_account_group_members_self ON user_account_group_members;
CREATE POLICY user_account_group_members_own_groups ON user_account_group_members FOR ALL TO coffer_app
    USING (
        ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                       WHERE ulg.user_id = current_app_user_id())
        AND EXISTS (SELECT 1 FROM user_account_groups g
                     WHERE g.id = user_account_group_members.group_id
                       AND g.user_id = current_app_user_id()))
    WITH CHECK (
        ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                       WHERE ulg.user_id = current_app_user_id())
        AND EXISTS (SELECT 1 FROM user_account_groups g
                     WHERE g.id = user_account_group_members.group_id
                       AND g.user_id = current_app_user_id()));

-- One FOR ALL policy served reads and writes, so the USING that let a member SEE every
-- shared report also let them DELETE it, and UPDATE it to themselves as owner. Reading
-- and writing are separate now: every member reads the ledger's shared reports and their
-- own, and only the owner changes or removes a report.
DROP POLICY saved_reports_per_user ON saved_reports;
CREATE POLICY saved_reports_read ON saved_reports FOR SELECT TO coffer_app
    USING (
        ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                       WHERE ulg.user_id = current_app_user_id())
        AND (visibility = 'ledger' OR owner_user_id = current_app_user_id()));
CREATE POLICY saved_reports_own ON saved_reports FOR ALL TO coffer_app
    USING (
        ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                       WHERE ulg.user_id = current_app_user_id())
        AND owner_user_id = current_app_user_id())
    WITH CHECK (
        ledger_id IN (SELECT ulg.ledger_id FROM user_ledger_grants ulg
                       WHERE ulg.user_id = current_app_user_id())
        AND owner_user_id = current_app_user_id());
