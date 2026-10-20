-- 190 — webauthn_pending_challenges: allow the 'masterkey-reveal' flow (ADR-0092 D2).
--
-- Revealing the master KEK requires a FRESH passkey assertion on top of the admin
-- session cookie: the cookie proves an admin authenticated some time in the last
-- 30 days, the assertion proves a human with an enrolled authenticator is present
-- now. That ceremony persists a pending challenge, and it gets its own flow value
-- rather than reusing 'login' so a challenge minted by the login endpoint can
-- never be redeemed for key material.
--
-- The flow CHECK (migration 016, widened for 'register' in 140 and 'invite' in
-- 176) must admit it; widen it the same way.

ALTER TABLE webauthn_pending_challenges
    DROP CONSTRAINT webauthn_pending_challenges_flow_check;

ALTER TABLE webauthn_pending_challenges
    ADD CONSTRAINT webauthn_pending_challenges_flow_check
    CHECK (flow IN ('setup', 'login', 'register', 'invite', 'masterkey-reveal'));
