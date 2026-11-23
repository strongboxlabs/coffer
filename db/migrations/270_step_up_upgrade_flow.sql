-- 270 — a passkey tap for starting an upgrade from the app (ADR-0110).
--
-- Starting an upgrade runs a new release on this install, so it needs a fresh tap of its own
-- (ADR-0105 D1), minted under its own challenge flow so a tap for another action cannot
-- approve it. The flow list is a closed set, widened here as in mig 248.
ALTER TABLE webauthn_pending_challenges
    DROP CONSTRAINT webauthn_pending_challenges_flow_check;

ALTER TABLE webauthn_pending_challenges
    ADD CONSTRAINT webauthn_pending_challenges_flow_check
    CHECK (flow IN (
        'setup',
        'login',
        'register',
        'invite',
        'masterkey-reveal',
        'backup-passphrase-reveal',
        'step-up:passkey-add',
        'step-up:passkey-delete',
        'step-up:recovery-codes',
        'step-up:mcp-token',
        'step-up:grant-admin',
        'step-up:restore',
        'step-up:backup-passphrase',
        'step-up:backup-delete',
        'step-up:mcp-writes',
        'step-up:upgrade'
    ));
