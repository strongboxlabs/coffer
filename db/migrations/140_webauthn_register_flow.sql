-- =============================================================================
-- 140 — webauthn_pending_challenges: allow the 'register' flow (ADR-0013)
-- =============================================================================
--
-- The add-a-passkey ceremony for an already-authenticated user
-- (POST /api/auth/register/begin + /complete) persists its in-flight
-- challenge with flow = 'register', distinct from 'setup' (which also creates
-- the user) and 'login' (assertion). The original CHECK in migration 016 only
-- allowed ('setup', 'login'); widen it.
--
-- No RLS/grant changes: the table's policies are unchanged; this only relaxes
-- the value domain of an existing column.
-- =============================================================================

ALTER TABLE webauthn_pending_challenges
    DROP CONSTRAINT webauthn_pending_challenges_flow_check;

ALTER TABLE webauthn_pending_challenges
    ADD CONSTRAINT webauthn_pending_challenges_flow_check
    CHECK (flow IN ('setup', 'login', 'register'));
