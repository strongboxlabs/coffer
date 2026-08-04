-- Phase 3 PR 3.4: short-lived state for in-flight WebAuthn ceremonies.
--
-- The browser registration / assertion ceremonies happen across two HTTP
-- requests (begin → complete). The server has to remember the challenge
-- bytes it issued at /begin so it can verify the response at /complete;
-- this table holds that state. Rows are short-lived (≤60s by default) and
-- consumed exactly once via UPDATE … RETURNING so two concurrent /complete
-- calls against the same challenge never both succeed.

CREATE TABLE webauthn_pending_challenges (
    id            UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    flow          TEXT         NOT NULL CHECK (flow IN ('setup', 'login')),
    -- user_id is NULL during the bootstrap setup flow (the user row
    -- doesn't exist yet — it's created at /complete in the same
    -- transaction as the credential). For login it's the resolved user.
    user_id       UUID         REFERENCES users(id) ON DELETE CASCADE,
    -- Fido2.AspNet's CredentialCreateOptions / AssertionOptions JSON
    -- serialisation. The challenge bytes live inside.
    options_json  TEXT         NOT NULL,
    -- Per-flow scratch: setup stores the proposed username + display name
    -- here so /complete can build the user row; login leaves it NULL.
    metadata_json TEXT,
    created_at    TIMESTAMPTZ  NOT NULL DEFAULT now(),
    expires_at    TIMESTAMPTZ  NOT NULL,
    consumed_at   TIMESTAMPTZ
);

CREATE INDEX idx_webauthn_pending_challenges_expires
    ON webauthn_pending_challenges (expires_at)
    WHERE consumed_at IS NULL;

COMMENT ON TABLE webauthn_pending_challenges IS
    'Server-side state for in-flight WebAuthn ceremonies (per ADR-0013). '
    'Rows live ~60s between /begin and /complete; consumed_at flips on '
    'first successful verify so a replay against the same challenge id '
    'fails. A periodic sweep (PR 3.5+) deletes expired rows.';
