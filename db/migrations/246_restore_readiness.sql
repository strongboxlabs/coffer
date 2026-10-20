-- ---------------------------------------------------------------------------
-- 246 — restore_readiness(): can this role restore a backup by swapping in a
--       new database?
--
-- A whole-database restore now builds the backup as a NEW database and swaps it
-- in with two renames (ADR-0061, amended 2026-10-09), instead of wiping the live
-- schema object by object. The wipe enumerated object kinds by hand; migration
-- 200's aggregate and 202's composite types were not on the list, and from
-- v0.62.0 every restore over an existing install failed in production while the
-- UI said "Restore complete".
--
-- The swap needs two things of the service role that a fresh install grants in
-- db/init and an existing one gets from scripts/provision-restore-privileges.sh:
-- CREATEDB, and ownership of the live database (ALTER DATABASE ... RENAME
-- requires both). This function reports them, so the admin and setup restore
-- endpoints can refuse BEFORE staging — with the fix — rather than after a
-- restart, from a log.
--
-- current_user / current_database() are the caller's (not SECURITY DEFINER):
-- the answer is about the role the API actually connects as.
--
-- Two more columns, for the boot that applies a restore:
--   locale_provider — the swap rebuilds a libc database only; an ICU one is
--     refused at staging rather than after the restart.
--   restore_marker  — the live database's COMMENT. A restore stamps the
--     database it built with 'coffer-restore:<attempt>:<replaced-name>' before
--     swapping it in, so a boot that died after the swap but before it cleared
--     the staging recognises its own restore instead of running it again (which
--     would rename the restored database away and prune the real way back).
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION restore_readiness()
RETURNS TABLE (
    role_name            text,
    database_name        text,
    can_create_databases boolean,
    owns_database        boolean,
    locale_provider      text,
    restore_marker       text
)
LANGUAGE sql
STABLE
AS $$
    SELECT current_user::text,
           current_database()::text,
           r.rolcreatedb,
           d.datdba = r.oid,
           d.datlocprovider::text,
           shobj_description(d.oid, 'pg_database')
      FROM pg_roles r
      JOIN pg_database d ON d.datname = current_database()
     WHERE r.rolname = current_user;
$$;

GRANT EXECUTE ON FUNCTION restore_readiness() TO coffer_service;
