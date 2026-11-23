-- 269 — upgrade_repo_token: the token the app reads a private fork's releases with (ADR-0110).
--
-- Only a private fork needs one; the public repository is read with none. It is sealed under
-- the master KEK, like the backup passphrase and the Drive token, so a backup carries it,
-- rotation re-wraps it and post-restore reconciliation discards one this install cannot open
-- (the admin enters it again). No row is written here: sealing needs the master key, which
-- SQL does not have. No row at all means no token.
--
-- Single-row by construction (id = 1). Service-role only, RLS deny-all.

CREATE TABLE upgrade_repo_token (
    id                SMALLINT    NOT NULL DEFAULT 1,
    -- The repository access token, sealed under the master KEK.
    token_ciphertext  BYTEA       NOT NULL,
    updated_at        TIMESTAMPTZ NOT NULL,
    updated_by        UUID        NULL,

    CONSTRAINT pk_upgrade_repo_token PRIMARY KEY (id),
    CONSTRAINT ck_upgrade_repo_token_singleton CHECK (id = 1)
);

COMMENT ON TABLE upgrade_repo_token IS
    'ADR-0110: the private-fork release token, sealed under the master KEK. '
    'Singleton; absent means no token. Re-wrapped by rotation; discarded by reconciliation '
    'when unopenable. RLS deny-all; service role only.';

ALTER TABLE upgrade_repo_token ENABLE ROW LEVEL SECURITY;
ALTER TABLE upgrade_repo_token FORCE  ROW LEVEL SECURITY;

REVOKE ALL ON TABLE upgrade_repo_token FROM coffer_app;
GRANT  ALL ON TABLE upgrade_repo_token TO   coffer_service;
