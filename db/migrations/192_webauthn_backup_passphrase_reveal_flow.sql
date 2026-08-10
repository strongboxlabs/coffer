-- 192 — webauthn_pending_challenges: allow the 'backup-passphrase-reveal' flow
--       (ADR-0092 D7).
--
-- The stored backup passphrase can now be revealed to an admin behind a fresh
-- passkey assertion, the same step-up the master-KEK reveal uses (migration 190).
-- It gets its own flow value rather than sharing 'masterkey-reveal': cross-redemption
-- between two admin step-ups gains an attacker nothing — both need the same session
-- and the same authenticator — but "a challenge is good for exactly the ceremony it
-- was minted for" is a flatter invariant than arguing the exception every time a
-- surface is added.
--
-- The flow CHECK (migration 016, widened in 140, 176 and 190) must admit it; widen
-- it the same way. This is the fourth such widening, which is why
-- admin_audit_events.action (migration 191) deliberately has no CHECK at all.

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
        'backup-passphrase-reveal'
    ));
