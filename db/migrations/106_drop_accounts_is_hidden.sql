-- Collapse accounts lifecycle to a single flag: drop accounts.is_hidden,
-- consolidating its currently-hidden rows under is_active=FALSE.
--
-- Bug context. The sidebar's "Show inactive" toggle surfaces
-- accounts where is_active=FALSE, expecting that to cover every
-- "user deactivated this" row. It didn't: nearly all deactivated accounts
-- carried is_hidden=TRUE AND is_active=FALSE and got filtered out
-- unconditionally by the sidebar grouping function (which also
-- drops is_hidden rows). Plus one outlier with is_active=TRUE AND
-- is_hidden=TRUE — the only row in the table where the two flags
-- disagree on direction. User reported "another fund family's 529
-- account" (one of them) as the canonical missing account.
--
-- Root cause is column conflation. Mig 012 introduced
-- accounts.is_hidden as the Moneydance `hide` flag with an
-- explicit "orthogonal to is_active" doc comment. ADR-0032 later
-- introduced is_active for the inactive-account lifecycle. In
-- practice the two flags collapsed: every is_hidden row is also
-- is_active=false (except the one outlier). The "orthogonal" model
-- never materialized in real data; meanwhile the dual-flag state
-- gives users two ways to mark an account "gone" and one of them
-- is invisible to the "Show inactive" toggle.
--
-- This migration:
--   1. Backfills is_active=FALSE on the one outlier row
--      (is_hidden=TRUE, is_active=TRUE). The user marked it
--      hidden, so the collapsed semantics treat it as inactive.
--   2. Drops accounts.is_hidden.
--
-- Companion code changes (same PR):
--   - AccountRow entity, AccountDtos DTO, AppDbContext mapping,
--     AccountsRepository projection lose the field.
--   - SPA AccountSummary type, AuthedSidebar / LedgerDetailPage /
--     accountPath filters lose the isHidden check.
--   - Test fixtures lose the isHidden property.
--
-- Note: txn_headers.is_hidden, txn_header_overrides.is_hidden, and
-- txn_legs.is_hidden are DIFFERENT columns on DIFFERENT tables and
-- remain. Only accounts.is_hidden is being dropped here.

BEGIN;

-- ---------------------------------------------------------------------------
-- 1. Backfill the single outlier — collapse to is_active=FALSE so the
--    new single-flag world reflects the user's hide-it intent.
-- ---------------------------------------------------------------------------
UPDATE accounts
   SET is_active = FALSE
 WHERE is_hidden = TRUE
   AND is_active = TRUE;

-- ---------------------------------------------------------------------------
-- 2. Drop the column. No indexes or FKs reference it.
-- ---------------------------------------------------------------------------
ALTER TABLE accounts DROP COLUMN is_hidden;

COMMIT;
