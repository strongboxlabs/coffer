-- =============================================================================
-- 143 — drive_sync.install_id: per-install Drive folder namespacing (ADR-0062)
-- =============================================================================
--
-- The Drive folder is found-or-created by name. With the `drive.file` scope an
-- install only ever sees the files it created, BUT two Coffer installs that reuse
-- the SAME OAuth client + Google account would resolve the SAME "Coffer Backups"
-- folder by name and commingle backups — and ④b+c remote retention would then
-- prune across both installs (data-loss hazard). To keep installs distinct, each
-- generates a stable, opaque install id once (on first connect) and names its
-- folder "Coffer Backups [<install_id>]". Surfaced in the admin UI so an operator
-- can tell which Drive folder belongs to which install.
--
-- Nullable + backfilled lazily in code (set on first connect, then never
-- cleared — kept across disconnect so a reconnect reuses the same folder).
-- =============================================================================

ALTER TABLE drive_sync ADD COLUMN install_id TEXT;

COMMENT ON COLUMN drive_sync.install_id IS
    'ADR-0062: stable opaque per-install id (set once on first connect, kept '
    'across disconnect). Folder is named "Coffer Backups [install_id]" so '
    'installs sharing one OAuth client + account stay in distinct folders.';
