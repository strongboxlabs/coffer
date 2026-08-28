-- 213 — RLS on the deployment-scope notification tables
--
-- Migration 207 created system_events and notification_subscribers and enabled RLS on
-- NEITHER, so coffer_app had unrestricted CRUD on both by way of the mig-017 ALTER
-- DEFAULT PRIVILEGES. One of them stores sealed delivery credentials.
--
-- WHY THAT MATTERED. The admin endpoints are the only app-role reader and they carry
-- RequireAuthorization(AuthPolicies.RequireAdmin), so there was no live escalation. But
-- this repo treats RLS as the backstop and the API filter as the primary check — mig
-- 174 and ADR-0083 exist because a filter is one forgotten attribute away from absent,
-- and the ledger notification endpoints proved that exact point in 0.65.0 by shipping
-- without RequireLedgerAccess(). A credentials table with no DB-level refusal is the
-- one place not to rely on the attribute being there.
--
-- WHY NOT SCOPED BY GRANT. These two tables are deployment scope (ADR-0096 D1): no
-- ledger_id, no grant to join. The right predicate is "is this app user an admin",
-- which is why the helper below exists.
--
-- WHY A SECURITY DEFINER HELPER rather than an inline subquery over users. `users`
-- itself has RLS (mig 017's users_self shows a caller only their own row), so an inline
-- `EXISTS (SELECT 1 FROM users WHERE id = current_app_user_id() AND is_admin)` would
-- happen to work — the row it needs is the caller's own — while silently coupling this
-- policy to the exact shape of users_self. Narrow that policy later and every
-- deployment-scope read starts returning nothing, for reasons nobody would connect. The
-- helper states the question once and answers it without depending on another policy.
--
-- It is deliberately narrow: no arguments, returns a boolean, reads one column of one
-- row identified by a session setting the app cannot forge (app.user_id is set by
-- AppUserDbConnectionInterceptor on a connection coffer_app cannot escalate).
--
-- NO CHANGE for coffer_service: it is BYPASSRLS, which is how the publisher and the
-- monitors keep working with no user behind them.
-- ---------------------------------------------------------------------------

BEGIN;

CREATE OR REPLACE FUNCTION current_app_user_is_admin() RETURNS BOOLEAN
LANGUAGE SQL STABLE SECURITY DEFINER
-- Empty search_path: a SECURITY DEFINER function must not resolve names through the
-- caller's path, or a coffer_app-created `users` in a schema earlier in that path would
-- answer this question instead of the real table.
SET search_path = pg_catalog, public
AS $$
    SELECT COALESCE(
        (SELECT u.is_admin FROM public.users u WHERE u.id = current_app_user_id()),
        FALSE);
$$;

COMMENT ON FUNCTION current_app_user_is_admin() IS
    'True when the app.user_id on this connection belongs to an admin. SECURITY DEFINER '
    'so it does not depend on the users_self RLS policy, and so a deployment-scope '
    'policy can ask the question without coupling to another policy''s shape (mig 213).';

GRANT EXECUTE ON FUNCTION current_app_user_is_admin() TO coffer_app, coffer_service;

-- ---------------------------------------------------------------------------
-- notification_subscribers — holds sealed delivery URLs (ADR-0096 D8).
-- ---------------------------------------------------------------------------
ALTER TABLE notification_subscribers ENABLE ROW LEVEL SECURITY;

CREATE POLICY notification_subscribers_admin ON notification_subscribers
    FOR ALL TO coffer_app
    USING      (current_app_user_is_admin())
    WITH CHECK (current_app_user_is_admin());

-- ---------------------------------------------------------------------------
-- system_events — the deployment's own event log. Read-only for the app role: it is
-- written by NotificationPublisher on the service role, and nothing user-facing has any
-- business inserting or amending an audit trail. Same treatment mig 174 gave the other
-- service-written logs, and the same reasoning as ledger_events in mig 208.
-- ---------------------------------------------------------------------------
ALTER TABLE system_events ENABLE ROW LEVEL SECURITY;

CREATE POLICY system_events_admin_read ON system_events
    FOR SELECT TO coffer_app
    USING (current_app_user_is_admin());

COMMIT;
