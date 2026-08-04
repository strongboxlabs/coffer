-- Phase 3 PR 3.2: WebAuthn / FIDO2 authentication schema (per ADR-0013).
--
-- The `users` table already exists from migration 014 as a Phase A
-- skeleton (id, display_name, last_opened_ledger_id, created_at). This
-- migration extends it with the columns the auth flow needs and adds the
-- companion tables that hold credentials, recovery codes, sessions, and
-- the one-shot bootstrap token.
--
-- All four companion tables are defined here even though sessions land in
-- PR 3.3 — they're one logical unit (the auth schema), and splitting them
-- across PRs would mean two migrations to coordinate against the same
-- conceptual change.

-- ---------------------------------------------------------------------------
-- 1) users: WebAuthn-related columns
-- ---------------------------------------------------------------------------
ALTER TABLE users
    ADD COLUMN username     TEXT,
    ADD COLUMN created_by   TEXT NOT NULL DEFAULT 'system',
    ADD COLUMN is_disabled  BOOLEAN NOT NULL DEFAULT FALSE;

-- The bootstrap "system" user seeded by migration 014 needs a username so
-- the partial unique index below has something to anchor on. Set a
-- well-known value rather than NULL so it can never be accidentally
-- claimed by a real user during registration.
UPDATE users SET username = 'system'
 WHERE id = '00000000-0000-0000-0000-000000000001';

CREATE UNIQUE INDEX uq_users_username ON users (username) WHERE username IS NOT NULL;

COMMENT ON COLUMN users.username IS
    'Display handle. Unique among non-NULL values; NULL allowed during '
    'the brief window between user-row creation and first credential '
    'registration, but the registration endpoints fail if it''s still '
    'NULL at credential-create time.';

COMMENT ON COLUMN users.created_by IS
    'Identifier of the actor that created this row — ''system'' for the '
    'bootstrap user, ''bootstrap-token'' for the first interactive '
    'register, the inviting user''s id for any later admin-issued accounts.';

COMMENT ON COLUMN users.is_disabled IS
    'Soft-disable flag. Disabled users keep their grants but cannot log '
    'in; revoke-and-delete is a separate destructive flow.';

-- ---------------------------------------------------------------------------
-- 2) webauthn_credentials: one row per registered authenticator
-- ---------------------------------------------------------------------------
CREATE TABLE webauthn_credentials (
    id                  UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id             UUID         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    -- The FIDO2 credential id is binary, opaque to us; uniqueness across
    -- the table is enforced because the same credential must never live
    -- under two users (replay-attack mitigation).
    credential_id       BYTEA        NOT NULL UNIQUE,
    public_key          BYTEA        NOT NULL,
    -- Signature counter starts at 0; increments per assertion. A counter
    -- that goes backwards on login indicates a cloned authenticator and
    -- the assertion is rejected (Fido2.AspNet handles this check; we just
    -- have to round-trip the value faithfully).
    signature_counter   BIGINT       NOT NULL DEFAULT 0,
    aaguid              UUID,
    transports          TEXT[],
    -- User-supplied label so the credentials list reads "YubiKey 5C
    -- (daily)" rather than a hex blob.
    nickname            TEXT         NOT NULL,
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT now(),
    last_used_at        TIMESTAMPTZ
);

CREATE INDEX idx_webauthn_credentials_user ON webauthn_credentials (user_id);

COMMENT ON TABLE webauthn_credentials IS
    'One row per WebAuthn / FIDO2 credential registered to a user. The '
    'auth ceremony per ADR-0013 — user gets one of these per device they '
    'register. Multiple credentials per user are first-class.';

-- ---------------------------------------------------------------------------
-- 3) recovery_codes: Argon2id-hashed one-shot codes
-- ---------------------------------------------------------------------------
CREATE TABLE recovery_codes (
    id          UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     UUID         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    -- Argon2id PHC string ($argon2id$v=19$m=...$t=...$p=...$salt$hash).
    -- Verification reads parameters out of the string so increasing the
    -- cost is a one-line change without a migration.
    code_hash   TEXT         NOT NULL,
    used_at     TIMESTAMPTZ,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX idx_recovery_codes_user_unused ON recovery_codes (user_id) WHERE used_at IS NULL;

COMMENT ON TABLE recovery_codes IS
    'Argon2id-hashed one-shot recovery codes (10 issued at registration '
    'or regeneration per ADR-0013). Each row''s code_hash is sufficient '
    'to verify a presented plaintext code; used_at flips on consumption '
    'so a code can never be reused.';

-- ---------------------------------------------------------------------------
-- 4) auth_sessions: cookie-backed sessions (used by PR 3.3)
-- ---------------------------------------------------------------------------
CREATE TABLE auth_sessions (
    id              UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         UUID         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    -- SHA-256 of the opaque random session id sent in the cookie. The
    -- plaintext never lives in the DB; an attacker with read access to
    -- this table cannot mint sessions.
    session_hash    BYTEA        NOT NULL UNIQUE,
    user_agent      TEXT,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT now(),
    last_seen_at    TIMESTAMPTZ  NOT NULL DEFAULT now(),
    expires_at      TIMESTAMPTZ  NOT NULL,
    revoked_at      TIMESTAMPTZ
);

CREATE INDEX idx_auth_sessions_user_active ON auth_sessions (user_id)
    WHERE revoked_at IS NULL;

COMMENT ON TABLE auth_sessions IS
    'Active and historical login sessions. Cookie carries an opaque ID; '
    'this table stores SHA-256(id) so DB reads cannot forge sessions. '
    'Defaults per ADR-0013: 30-day max lifetime (expires_at), 7-day idle '
    'timeout (enforced by application against last_seen_at).';

-- ---------------------------------------------------------------------------
-- 5) bootstrap_tokens: one-shot setup tokens (used by PR 3.2)
-- ---------------------------------------------------------------------------
CREATE TABLE bootstrap_tokens (
    -- Hash of the plaintext token, which is logged once at startup and
    -- never persisted. SHA-256 (32 bytes) — the table holds one row at a
    -- time so collision resistance is the only requirement.
    token_hash  BYTEA        PRIMARY KEY,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now(),
    expires_at  TIMESTAMPTZ  NOT NULL,
    consumed_at TIMESTAMPTZ
);

COMMENT ON TABLE bootstrap_tokens IS
    'One-shot tokens minted at API startup when no WebAuthn credentials '
    'exist. The plaintext is written to the API logs once and consumed '
    'by /api/auth/setup/{token}. Subsequent registrations require an '
    'authenticated session or a recovery code (per ADR-0013).';
