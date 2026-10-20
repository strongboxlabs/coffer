-- 253 — oidc_server_keys: the OAuth server's signing and encryption keys, in the database.
--
-- They were two plaintext files under data/openiddict (signing.pem, encryption.key), on
-- the data volume but outside the database, so no backup carried them: restored onto a
-- new host, every MCP token failed with ID2004 and every connector had to re-authorize.
-- The files were also written 0644 and non-atomically, so a crash mid-write of
-- signing.pem bricked boot (FU-069).
--
-- Now sealed under the master KEK, like the backup passphrase and the Drive token, so a
-- backup takes them, rotation re-wraps them and post-restore reconciliation replaces a
-- pair this install cannot open. The first boot of this build moves existing files in and
-- deletes them; an install without files mints a pair. No row is written here: sealing
-- needs the master key, which SQL does not have.
--
-- Single-row by construction (id = 1). Service-role only, RLS deny-all.

CREATE TABLE oidc_server_keys (
    id                        SMALLINT    NOT NULL DEFAULT 1,
    -- RSA-2048 private key (PKCS#1 DER) that signs tokens, sealed under the master KEK.
    signing_key_ciphertext    BYTEA       NOT NULL,
    -- 256-bit symmetric key that encrypts token payloads, sealed under the master KEK.
    encryption_key_ciphertext BYTEA       NOT NULL,
    created_at                TIMESTAMPTZ NOT NULL,

    CONSTRAINT pk_oidc_server_keys PRIMARY KEY (id),
    CONSTRAINT ck_oidc_server_keys_singleton CHECK (id = 1)
);

COMMENT ON TABLE oidc_server_keys IS
    'FU-069: the OAuth server''s signing and encryption keys, sealed under the master KEK. '
    'Singleton. Re-wrapped by rotation; replaced by reconciliation when unopenable. '
    'RLS deny-all; service role only.';

ALTER TABLE oidc_server_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE oidc_server_keys FORCE  ROW LEVEL SECURITY;

REVOKE ALL ON TABLE oidc_server_keys FROM coffer_app;
GRANT  ALL ON TABLE oidc_server_keys TO   coffer_service;
