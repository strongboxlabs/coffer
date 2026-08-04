-- =============================================================================
-- 145 — mcp_access_tokens (ADR-0063): revocable bearer tokens for the MCP server
-- =============================================================================
--
-- The MCP read-only report server (/mcp) authenticates remote AI clients with
-- opaque reference tokens (ADR-0063 §D7 "revocable reference tokens"), not the
-- browser session cookie. A client (Claude Desktop, etc.) presents the token as
-- `Authorization: Bearer <token>`; the API hashes it and looks it up here.
--
-- Like auth_sessions (migration 015) the plaintext NEVER lives in the DB — only
-- its SHA-256 — so read access to this table cannot mint a working token. The
-- token authenticates ONLY the /mcp endpoint (its own auth scheme + policy); it
-- is not accepted by the REST API, so a read-only token can never reach a
-- mutation endpoint. Scope is recorded for forward-compatibility (v1 issues only
-- `coffer.read`).
--
-- The token resolves to its owning user, whose grants + RLS remain the data
-- boundary exactly as for a cookie session — the MCP tools run as that user.
-- =============================================================================

CREATE TABLE mcp_access_tokens (
    id            UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id       UUID         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    -- User-supplied label so the "Connected apps" list reads "Claude Desktop
    -- (laptop)" rather than an opaque id.
    name          TEXT         NOT NULL,
    -- SHA-256 of the opaque random token string sent in the Authorization
    -- header. Unique so a presented hash is a single-row lookup.
    token_hash    BYTEA        NOT NULL UNIQUE,
    -- Space-separated OAuth-style scopes. v1 is read-only: 'coffer.read'.
    scopes        TEXT         NOT NULL DEFAULT 'coffer.read',
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT now(),
    last_used_at  TIMESTAMPTZ,
    -- NULL = never expires (lives until revoked). Otherwise the auth handler
    -- treats a past expiry the same as revoked / unknown.
    expires_at    TIMESTAMPTZ,
    revoked_at    TIMESTAMPTZ,

    CONSTRAINT ck_mcp_access_tokens_name CHECK (name <> '')
);

CREATE INDEX idx_mcp_access_tokens_user_active ON mcp_access_tokens (user_id)
    WHERE revoked_at IS NULL;

COMMENT ON TABLE mcp_access_tokens IS
    'ADR-0063: revocable bearer tokens for the MCP read-only report server. The '
    'plaintext is shown to the user once at issue and never persisted; this table '
    'stores SHA-256(token) so a DB read cannot forge one. Authenticates only the '
    '/mcp endpoint (not the REST API). Resolves to the owning user — grants + RLS '
    'are the data boundary, as for a cookie session.';

-- RLS — own-user only (the user_preferences pattern, migration 134). The auth
-- handler's pre-auth hash lookup runs as coffer_service (RLS-bypassing), exactly
-- like cookie validation in auth_sessions, because app.user_id isn't set yet at
-- authentication time. The management endpoints scope by the authenticated
-- user explicitly AND this policy applies — defence in depth.
ALTER TABLE mcp_access_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE mcp_access_tokens FORCE  ROW LEVEL SECURITY;

DROP POLICY IF EXISTS mcp_access_tokens_per_user ON mcp_access_tokens;
CREATE POLICY mcp_access_tokens_per_user ON mcp_access_tokens
    FOR ALL
    TO coffer_app
    USING (user_id = current_app_user_id())
    WITH CHECK (user_id = current_app_user_id());

GRANT SELECT, INSERT, UPDATE, DELETE ON mcp_access_tokens TO coffer_app;
GRANT ALL ON mcp_access_tokens TO coffer_service;
