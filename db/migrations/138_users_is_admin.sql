-- =============================================================================
-- 138 — users.is_admin (ADR-0060): operator/admin role
-- =============================================================================
--
-- A global admin flag on users. Admin gates system-wide, cross-user actions
-- (first consumer: whole-DB encrypted backup, ADR-0060) — distinct from the
-- per-ledger grants in user_ledger_grants, which scope access to one ledger.
--
-- "First user is admin": the human operator who completes the first-run setup
-- ceremony becomes admin (enforced going forward in the /setup/.../complete
-- handler). This migration backfills that rule for an already-provisioned
-- install — the earliest-created human user (the system service identity
-- 0000…0001 is excluded; it is an unattended worker, not an operator).
--
-- No new RLS/grants: is_admin is a column on the already-secured users table
-- (users_self policy — a user sees only their own row).
-- =============================================================================

ALTER TABLE users
    ADD COLUMN is_admin BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN users.is_admin IS
    'ADR-0060: global operator/admin flag. Gates system-wide cross-user '
    'actions (e.g. whole-DB backup). Set for the first human user at '
    'setup-complete. Distinct from per-ledger user_ledger_grants.';

-- Backfill: the earliest human user (if any) is the operator → admin. The
-- system service user is never an admin. No-op on a fresh install.
UPDATE users
SET is_admin = TRUE
WHERE id = (
    SELECT id
    FROM users
    WHERE id <> '00000000-0000-0000-0000-000000000001'
    ORDER BY created_at, id
    LIMIT 1
);

-- Privilege boundary: is_admin may be set ONLY by the service role
-- (setup-complete, migrations) — never by the request-time coffer_app role.
-- The users_self RLS policy is FOR ALL, so without this a user could
-- `UPDATE users SET is_admin = true` on their own row and self-promote.
-- coffer_app legitimately self-updates only last_opened_ledger_id, so revoke
-- its table-wide UPDATE on users and grant back just that column. (Future
-- profile-edit columns add their own column grant when they land.)
REVOKE UPDATE ON users FROM coffer_app;
GRANT UPDATE (last_opened_ledger_id) ON users TO coffer_app;
