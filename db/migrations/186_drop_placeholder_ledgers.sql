-- =============================================================================
-- 186 — drop the migration-seeded placeholder ledgers (ADR-0088)
-- =============================================================================
--
-- Migration 055 created an empty `Demo` ledger (…0002) alongside the long-
-- standing empty `Default` (…0001), on the assumption that `coffer-api
-- provision --mode <clean|demo>` would resolve them before the first user
-- existed: `clean` deleted both, `demo` seeded Demo from the bundled dataset.
--
-- That assumption did not hold. scripts/dev-up-docker.sh — the documented dev
-- path — never calls provision, so every fresh install kept BOTH placeholders.
-- The setup page lists ledgers straight from this table, so it offered "Default"
-- and "Demo" as if they held data. Picking either granted ownership of an empty
-- shell: no accounts, and no categories either, because the starter-category
-- seed deliberately skips the join-existing path ("joining an existing ledger
-- inherits its categories" — true of a real ledger, false of a placeholder).
--
-- ADR-0088 removes the placeholders entirely. Install shape is now a single
-- question at setup ("include a Demo ledger?"), and every ledger a user ends up
-- with is created through the app — so it always gets starter categories, or in
-- Demo's case the sample dataset's own category tree.
--
-- SAFETY. This must never delete a populated Default on a live install, where
-- …0001 holds everything. Three guards, all required:
--   * no human credentials exist  — restricts this to a pre-first-user install,
--     mirroring ProvisioningService.HasHumanUsersAsync
--   * the ledger has no accounts
--   * the ledger has no transaction headers
-- On any install that is already in use the WHERE clause matches nothing and
-- this migration is a no-op. Grants cascade with the ledger row; the system
-- user row is untouched (the CLI importer's owner-grant FK depends on it).
--
-- Single autocommitting statement — safe under DbUp NoTransaction. Idempotent:
-- re-running finds the rows already gone.
-- =============================================================================

DELETE FROM ledgers l
WHERE l.id IN (
        '00000000-0000-0000-0000-000000000001',
        '00000000-0000-0000-0000-000000000002'
      )
  AND NOT EXISTS (SELECT 1 FROM webauthn_credentials)
  AND NOT EXISTS (SELECT 1 FROM accounts     a WHERE a.ledger_id = l.id)
  AND NOT EXISTS (SELECT 1 FROM txn_headers  h WHERE h.ledger_id = l.id);
