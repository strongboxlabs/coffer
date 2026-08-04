-- 176 — webauthn_pending_challenges: allow the 'invite' flow (ADR-0083 slice B).
--
-- The invite-redeem ceremony (InvitesEndpoints) persists a pending WebAuthn
-- registration challenge with flow='invite' — a scoped, repeatable clone of the
-- first-user setup ceremony. The flow CHECK (migration 016, widened for 'register'
-- in migration 140) must admit it; widen it the same way.

ALTER TABLE webauthn_pending_challenges
    DROP CONSTRAINT webauthn_pending_challenges_flow_check;

ALTER TABLE webauthn_pending_challenges
    ADD CONSTRAINT webauthn_pending_challenges_flow_check
    CHECK (flow IN ('setup', 'login', 'register', 'invite'));
