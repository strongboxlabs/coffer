-- SimpleFIN slice 1 (Phase 5). Three additions to `feed_connections`
-- so the POST endpoint can persist a connection end-to-end:
--
--   * access_url_ciphertext BYTEA  — the SimpleFIN access URL sealed
--     under the owning ledger's LEK per ADR-0026. Layout:
--     nonce(12) || ciphertext(N) || tag(16). NEVER plaintext.
--
--   * institution_name TEXT  — the FI display name surfaced from
--     SimpleFIN's /info response (or any account's org.name on first
--     sync). NULL until populated; the SPA falls back to "SimpleFIN"
--     for unset rows.
--
--   * created_by_user_id UUID — audit trail: which user clicked
--     "Connect" on this row. NULL only on rows that pre-date this
--     migration (none today, but the column is nullable defensively).
--
-- The other shape (provider CHECK, status CHECK, ledger_id FK, RLS
-- policy) was already established in earlier migrations.

ALTER TABLE feed_connections
    ADD COLUMN access_url_ciphertext BYTEA,
    ADD COLUMN institution_name      TEXT,
    ADD COLUMN created_by_user_id    UUID REFERENCES users(id) ON DELETE SET NULL;

COMMENT ON COLUMN feed_connections.access_url_ciphertext IS
    'SimpleFIN access URL sealed under the owning ledger''s LEK '
    '(ADR-0026). Layout: AES-GCM nonce(12) || ciphertext || tag(16). '
    'NULL only on rows that pre-date this migration (none in '
    'practice — every freshly-created connection writes it).';

COMMENT ON COLUMN feed_connections.institution_name IS
    'Display name for the FI behind this feed connection (SimpleFIN '
    '/info or any account''s org.name on first sync). NULL on rows '
    'awaiting first sync; the SPA falls back to "SimpleFIN" until '
    'this populates.';

COMMENT ON COLUMN feed_connections.created_by_user_id IS
    'User who initiated this feed connection. Audit only — '
    'ON DELETE SET NULL so a removed user doesn''t cascade their '
    'feed history.';
