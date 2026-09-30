-- ---------------------------------------------------------------------------
-- 231 — row-level security on the snapshot tables (and `invites`).
--
-- `ledger_snapshots` holds a complete copy of every row of a ledger — the same
-- rows its source tables protect with RLS — and had none. Neither did
-- `ledger_snapshot_parts` (migration 193), which followed the same posture
-- rather than introducing a second one for the same data. Both were gated only
-- by the API's LedgerAuthorizer, so anything reaching the database as
-- `coffer_app` outside that gate could read any ledger's full contents.
--
-- `invites` is included because it is the same defect: it carries `ledger_id`,
-- had RLS off, and no rationale for the exemption exists in migration 175 that
-- created it or in ADR-0083. The remaining tables without RLS are infrastructure
-- and stay that way: OpenIddict's four, `__schema_migrations`, and
-- `bootstrap_tokens` — none is ledger-scoped.
--
-- WHY THE WRITE POLICY IS NOT owner/editor HERE, unlike its siblings.
-- `POST /snapshots` and `DELETE /snapshots/{id}` require only ledger VISIBILITY
-- today; restore is the one that carries `.AsLedgerOwner()`. Gating writes to
-- owner/editor would therefore not be defence in depth — it would silently take
-- away something a viewer can do now, from inside a migration. These policies
-- enforce the boundary that is actually missing, which is CROSS-LEDGER: you must
-- hold some grant on the ledger. Whether a viewer should be able to create or
-- delete a snapshot is a real question, and an endpoint-level one.
--
-- SAFETY NOTE FOR CAPTURE AND RESTORE. Both run as the CALLER (`coffer_app`),
-- not SECURITY DEFINER, so a policy the request context cannot satisfy would not
-- error — it would make capture quietly write nothing and restore quietly find
-- nothing. The auto-snapshot path is unaffected either way: SchedulerService
-- builds its context from ServiceDbContextFactory (`coffer_service`, BYPASSRLS).
-- The request path always has `app.user_id` set and a grant on the ledger, since
-- LedgerAuthorizer has already resolved visibility before the repository runs.
-- ---------------------------------------------------------------------------

ALTER TABLE ledger_snapshots ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS ledger_snapshots_read ON ledger_snapshots;
CREATE POLICY ledger_snapshots_read ON ledger_snapshots
    FOR SELECT TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
         WHERE ulg.user_id = current_app_user_id()));

DROP POLICY IF EXISTS ledger_snapshots_write ON ledger_snapshots;
CREATE POLICY ledger_snapshots_write ON ledger_snapshots
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
         WHERE ulg.user_id = current_app_user_id()))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
         WHERE ulg.user_id = current_app_user_id()));

GRANT SELECT, INSERT, UPDATE, DELETE ON ledger_snapshots TO coffer_app;

-- ---------------------------------------------------------------------------

-- `ledger_snapshot_parts` has NO `ledger_id` — it reaches its ledger
-- transitively through `snapshot_id`, which is also why SchemaDriftGuardTests
-- excludes it (that guard classifies tables carrying `ledger_id`). So the policy
-- cannot be keyed the way its siblings are; it joins through the parent.
--
-- Written as an explicit grant check rather than relying on the parent's own RLS
-- to filter the subquery. Postgres does apply RLS inside a policy's subquery, so
-- `EXISTS (SELECT 1 FROM ledger_snapshots s WHERE s.id = snapshot_id)` would
-- work — but a security policy should state what it requires instead of
-- inheriting it from somewhere else by a rule the next reader has to know.
ALTER TABLE ledger_snapshot_parts ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS ledger_snapshot_parts_read ON ledger_snapshot_parts;
CREATE POLICY ledger_snapshot_parts_read ON ledger_snapshot_parts
    FOR SELECT TO coffer_app
    USING (EXISTS (
        SELECT 1 FROM ledger_snapshots s
          JOIN user_ledger_grants ulg ON ulg.ledger_id = s.ledger_id
         WHERE s.id = ledger_snapshot_parts.snapshot_id
           AND ulg.user_id = current_app_user_id()));

DROP POLICY IF EXISTS ledger_snapshot_parts_write ON ledger_snapshot_parts;
CREATE POLICY ledger_snapshot_parts_write ON ledger_snapshot_parts
    TO coffer_app
    USING (EXISTS (
        SELECT 1 FROM ledger_snapshots s
          JOIN user_ledger_grants ulg ON ulg.ledger_id = s.ledger_id
         WHERE s.id = ledger_snapshot_parts.snapshot_id
           AND ulg.user_id = current_app_user_id()))
    WITH CHECK (EXISTS (
        SELECT 1 FROM ledger_snapshots s
          JOIN user_ledger_grants ulg ON ulg.ledger_id = s.ledger_id
         WHERE s.id = ledger_snapshot_parts.snapshot_id
           AND ulg.user_id = current_app_user_id()));

GRANT SELECT, INSERT, UPDATE, DELETE ON ledger_snapshot_parts TO coffer_app;

-- ---------------------------------------------------------------------------

ALTER TABLE invites ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS invites_read ON invites;
CREATE POLICY invites_read ON invites
    FOR SELECT TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
         WHERE ulg.user_id = current_app_user_id()));

-- Invites are administered by the owner — POST/DELETE /api/admin/invites sits
-- behind the owner check (ADR-0083) — so here the owner/editor shape of the
-- sibling tables costs nothing and matches the endpoint.
DROP POLICY IF EXISTS invites_write ON invites;
CREATE POLICY invites_write ON invites
    TO coffer_app
    USING (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
         WHERE ulg.user_id = current_app_user_id()
           AND ulg.role = ANY (ARRAY['owner'::text, 'editor'::text])))
    WITH CHECK (ledger_id IN (
        SELECT ulg.ledger_id FROM user_ledger_grants ulg
         WHERE ulg.user_id = current_app_user_id()
           AND ulg.role = ANY (ARRAY['owner'::text, 'editor'::text])));

GRANT SELECT, INSERT, UPDATE, DELETE ON invites TO coffer_app;
