-- 175 — Invite links (ADR-0083 slice B). A generalized, repeatable, SCOPED
-- bootstrap token: same token crypto/storage as bootstrap_tokens (mig 015) —
-- the plaintext is shown to the issuer ONCE and never persisted; the table holds
-- the raw SHA-256 (32 bytes) as the PK. Adds scope columns: who issued it, the
-- target ledger + grant role it confers (both NULL = an instance-only invite
-- that just creates the account), an optional instance-admin grant, expiry, and
-- a single-use `consumed_at` flip.
--
-- Service-role only: redeem runs PRE-AUTH (the token IS the credential), so
-- coffer_app is never granted — every read/write goes through coffer_service,
-- exactly like bootstrap_tokens.

CREATE TABLE invites (
    token_hash         BYTEA        PRIMARY KEY,
    -- Public, non-secret handle for list / revoke (the token_hash is derived
    -- from the secret; never expose it). App-generated.
    id                 UUID         NOT NULL UNIQUE,
    issued_by_user_id  UUID         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    ledger_id          UUID         REFERENCES ledgers(id) ON DELETE CASCADE,
    role               TEXT         CHECK (role IN ('owner', 'editor', 'viewer')),
    grants_admin       BOOLEAN      NOT NULL DEFAULT FALSE,
    expires_at         TIMESTAMPTZ  NOT NULL,
    consumed_at        TIMESTAMPTZ,
    created_at         TIMESTAMPTZ  NOT NULL DEFAULT now(),

    -- A ledger invite carries a role; an instance-only invite carries neither.
    CONSTRAINT invites_ledger_role_together CHECK ((ledger_id IS NULL) = (role IS NULL))
);

CREATE INDEX ix_invites_issued_by ON invites (issued_by_user_id);
CREATE INDEX ix_invites_ledger ON invites (ledger_id) WHERE ledger_id IS NOT NULL;

GRANT ALL ON invites TO coffer_service;
-- (intentionally NO grant to coffer_app — service-role only, like bootstrap_tokens.)
