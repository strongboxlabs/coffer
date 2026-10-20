-- ---------------------------------------------------------------------------
-- 248 — sessions remember how they began; a disabled user is not an admin;
--       one step-up flow per action; setup completes once.
--
-- Four changes for the 2026-10-12 security audit (ADR-0105).
--
-- 1. auth_sessions.auth_method. Sensitive actions now need a fresh passkey tap,
--    and that tap must come from a passkey OLDER than the session making the
--    request: otherwise a stolen cookie could enrol a passkey of its own and then
--    approve anything with it. The one exception is a session that began with a
--    recovery code, which may add a single passkey within 15 minutes without a
--    tap (it may have no usable passkey at all — that is why it used a recovery
--    code). The exception needs to know how the session began.
--
--    Existing sessions are recorded as 'passkey': the conservative reading, since
--    it grants no exception. Nobody is signed out by this migration.
--
-- 2. current_app_user_is_admin() ignored users.is_disabled, so a disabled admin
--    kept reading system_events and the admin-only notification targets through
--    RLS. Disabling a user now also revokes their sessions and tokens (API side);
--    this closes the database side of the same gap.
-- ---------------------------------------------------------------------------

ALTER TABLE auth_sessions
    ADD COLUMN auth_method TEXT NOT NULL DEFAULT 'passkey',
    ADD CONSTRAINT ck_auth_sessions_auth_method
        CHECK (auth_method IN ('passkey', 'recovery-code', 'setup', 'invite', 'dev'));

COMMENT ON COLUMN auth_sessions.auth_method IS
    'How the session began (mig 248). A recovery-code session may add one passkey '
    'within 15 minutes without a step-up tap; every other sensitive action needs a '
    'tap from a passkey older than the session.';

-- 3. One step-up flow per gated action. A challenge is good for exactly the action
--    it was minted for (mig 192's invariant), so a tap approving "delete this
--    backup" can't be replayed to approve a restore. Listed rather than matched by
--    prefix: a new gated action is a deliberate widening, here, like every other.
ALTER TABLE webauthn_pending_challenges
    DROP CONSTRAINT webauthn_pending_challenges_flow_check;

ALTER TABLE webauthn_pending_challenges
    ADD CONSTRAINT webauthn_pending_challenges_flow_check
    CHECK (flow IN (
        'setup',
        'login',
        'register',
        'invite',
        'masterkey-reveal',
        'backup-passphrase-reveal',
        'step-up:passkey-add',
        'step-up:passkey-delete',
        'step-up:recovery-codes',
        'step-up:mcp-token',
        'step-up:grant-admin',
        'step-up:restore',
        'step-up:backup-passphrase',
        'step-up:backup-delete',
        'step-up:mcp-writes'
    ));

CREATE OR REPLACE FUNCTION current_app_user_is_admin() RETURNS BOOLEAN
LANGUAGE SQL STABLE SECURITY DEFINER
-- Fixed search_path: a SECURITY DEFINER function must not resolve names through the
-- caller's path, or a coffer_app-created `users` in a schema earlier in that path would
-- answer this question instead of the real table.
SET search_path = pg_catalog, public
AS $$
    SELECT COALESCE(
        (SELECT u.is_admin AND NOT u.is_disabled
           FROM public.users u
          WHERE u.id = current_app_user_id()),
        FALSE);
$$;

COMMENT ON FUNCTION current_app_user_is_admin() IS
    'True when the app.user_id on this connection belongs to an ENABLED admin. SECURITY '
    'DEFINER so it does not depend on the users_self RLS policy, and so a deployment-scope '
    'policy can ask the question without coupling to another policy''s shape (mig 213; '
    'disabled users excluded since mig 248).';

-- 4. The setup.completed marker. First-run setup re-armed whenever no passkey
--    existed, so an install whose user deleted their last passkey (recovery codes
--    kept) offered pre-auth setup — and its whole-database restore — to whoever read
--    the next setup URL. Setup now writes this marker with the first user, and only
--    its absence offers setup. Backfilled here for every install that already has a
--    user, so no upgrade ever re-arms; a fresh install has only the system user and
--    gets nothing.
INSERT INTO system_settings (key, value)
SELECT 'setup.completed', 'true'::jsonb
 WHERE EXISTS (SELECT 1 FROM users
                WHERE id <> '00000000-0000-0000-0000-000000000001'::uuid)
ON CONFLICT (key) DO NOTHING;
