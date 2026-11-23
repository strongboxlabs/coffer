-- =============================================================================
-- 278 — a feed connection's name says what sets it
-- =============================================================================
--
-- WHY
-- ---
-- The catalog comment on feed_connections.institution_name (migration 036) says the
-- column is filled from SimpleFIN's /info or from an account's org.name on the first
-- sync. Nothing does that. The column is written in two places only: once when the
-- connection is made, from the connection probe, and whenever the ledger owner renames
-- the connection. A sync never touches it. The probe also no longer names a connection
-- that reaches several institutions after one of them.
--
-- WHAT CHANGES
-- ------------
-- Only the column's comment is replaced, so the catalog says what the application does.
-- No table, row, index, function or grant changes.
--
-- EXISTING INSTALLS
-- -----------------
-- Nothing to do. A fresh install and an upgraded one end with the same comment, because
-- both run this migration after 036.
-- =============================================================================

BEGIN;

COMMENT ON COLUMN feed_connections.institution_name IS
    'The connection''s name. Set when the connection is made: the institution''s name '
    'when every account the connection reaches is at one institution, otherwise NULL. '
    'The ledger owner can rename it (1 to 80 characters). A sync never changes it. '
    'The SPA shows NULL as "SimpleFIN".';

COMMIT;
