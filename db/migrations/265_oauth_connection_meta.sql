-- =============================================================================
-- 265 — oauth_connection_meta: a name and a last-used time for each OAuth connection
-- =============================================================================
--
-- WHY
-- ---
-- An app that connects to /mcp through OAuth is one OpenIddict authorization per user
-- per client. Two things people need to tell their connections apart sit outside
-- OpenIddict's schema:
--
--   * a name. The client registers itself under a name of its own choosing, and every
--     install of one client reports the same string, so two laptops running one app are
--     two rows both called the same. The owner needs to rename theirs; an admin looking
--     across users needs to see that name beside the owner's.
--   * when it was last used. Personal MCP tokens have carried last_used_at since mig 145.
--     OpenIddict records nothing of the kind on an authorization, so without this an
--     admin cannot tell a connection in daily use from one nobody has touched in months.
--
-- WHY A TABLE, NOT OpenIddict's Properties BAG
-- --------------------------------------------
-- last_used_at is written on every authenticated /mcp request. The bag is one JSON text
-- column on the authorization row, so stamping it is a read-modify-write of the whole
-- value: two concurrent requests lose one of the writes, and the same write would race
-- OpenIddict's own updates to that row (status, concurrency token). A row of our own
-- takes a plain single-column UPDATE.
--
-- KEY AND LIFETIME
-- ----------------
-- Keyed on the authorization id, so there is at most one row per connection, and
-- referencing "OpenIddictAuthorizations" with ON DELETE CASCADE: removing an authorization
-- (an admin revoking a whole client registration) takes its row with it, and a stamp for
-- an authorization that does not exist is refused rather than orphaned. A REVOKED
-- authorization keeps its row; it is no longer listed, so nothing reads it.
--
-- A connection that existed before this migration has no row. Its label reads as the
-- client's own name and its last-used time as unknown ("never recorded") until it is next
-- used. Nothing is backfilled: there is no honest value to derive, and a stamped now()
-- would claim a use that did not happen.
--
-- RLS: own-user only, the mcp_access_tokens pattern (mig 145). The /mcp stamp runs after
-- authentication and the admin list reads across users, so both go through coffer_service
-- with an explicit user_id; the policy is defence in depth for any coffer_app path.
-- =============================================================================

CREATE TABLE oauth_connection_meta (
    authorization_id  TEXT         PRIMARY KEY
                                   REFERENCES "OpenIddictAuthorizations" ("Id") ON DELETE CASCADE,
    user_id           UUID         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    -- Owner-chosen name. NULL = none set: the client's own registered name is shown.
    label             TEXT,
    -- Last authenticated /mcp request on this connection. NULL = none recorded.
    last_used_at      TIMESTAMPTZ,

    CONSTRAINT ck_oauth_connection_meta_label CHECK (label IS NULL OR label <> '')
);

CREATE INDEX idx_oauth_connection_meta_user ON oauth_connection_meta (user_id);

COMMENT ON TABLE oauth_connection_meta IS
    'Owner-chosen name and last-used time for an OAuth connection to /mcp, one row per '
    'OpenIddict authorization. Outside the OpenIddict schema so the per-request last-used '
    'stamp is a single-column UPDATE, not a read-modify-write of its Properties bag.';

ALTER TABLE oauth_connection_meta ENABLE ROW LEVEL SECURITY;
ALTER TABLE oauth_connection_meta FORCE  ROW LEVEL SECURITY;

DROP POLICY IF EXISTS oauth_connection_meta_per_user ON oauth_connection_meta;
CREATE POLICY oauth_connection_meta_per_user ON oauth_connection_meta
    FOR ALL
    TO coffer_app
    USING (user_id = current_app_user_id())
    WITH CHECK (user_id = current_app_user_id());

GRANT SELECT, INSERT, UPDATE, DELETE ON oauth_connection_meta TO coffer_app;
GRANT ALL ON oauth_connection_meta TO coffer_service;
