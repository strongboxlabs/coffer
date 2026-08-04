-- =============================================================================
-- 178 — mcp_tool_invocations two-phase lifecycle (ADR-0086 Track A)
-- =============================================================================
--
-- The MCP write-audit (migration 170, ADR-0081 D3) recorded one row per call
-- AFTER the tool returned, on the caller's cancellation token. A client timeout
-- (cancelled token) or a hang therefore recorded NOTHING — the writes an
-- oversight log most needs to capture were the ones silently dropped.
--
-- ADR-0086 makes the row two-phase: an attempt row is written BEFORE the tool
-- runs (status='pending'), so every committed change already has a row; it is
-- finalized to a terminal state AFTER the call. This migration adds the
-- lifecycle columns and backfills the existing (already-terminal) rows.
--
--   status       pending → the call started; outcome not yet recorded (or the
--                          process died before finalize — a visible unknown).
--                ok       → the tool completed without error.
--                error    → the tool reported an error or threw.
--                cancelled→ the call was cancelled / timed out (distinct from
--                          error, so a client timeout is unambiguous).
--   completed_at the finalize instant (NULL while pending).
--   trace_id     HttpContext.TraceIdentifier, correlating the row with the
--                application log line and the client's ProblemDetails traceId.
--
-- is_error is retained and kept in sync by the recorder (is_error = status =
-- 'error') so the existing admin viewer (ADR-0081 D5) is unaffected.
-- No grant/RLS change: coffer_service (the recorder's role) already has GRANT ALL
-- (migration 170); the two-phase writes run as coffer_service on
-- CancellationToken.None.
-- =============================================================================

ALTER TABLE mcp_tool_invocations
    ADD COLUMN status       TEXT        NOT NULL DEFAULT 'pending',
    ADD COLUMN completed_at TIMESTAMPTZ,
    ADD COLUMN trace_id     TEXT;

-- Backfill: every pre-existing row is a completed call recorded post-hoc, so it
-- is terminal — map is_error to the new status and treat created_at as the
-- completion instant (the old model had no separate start/finish).
UPDATE mcp_tool_invocations
   SET status       = CASE WHEN is_error THEN 'error' ELSE 'ok' END,
       completed_at = created_at
 WHERE completed_at IS NULL;

ALTER TABLE mcp_tool_invocations
    ADD CONSTRAINT ck_mcp_tool_invocations_status
    CHECK (status IN ('pending', 'ok', 'error', 'cancelled'));

-- A pending row has no completion instant; a terminal row must have one.
ALTER TABLE mcp_tool_invocations
    ADD CONSTRAINT ck_mcp_tool_invocations_completed_at
    CHECK ((status = 'pending') = (completed_at IS NULL));

COMMENT ON COLUMN mcp_tool_invocations.status IS
    'ADR-0086: pending (attempt, pre-call) | ok | error | cancelled (terminal, finalized post-call).';
COMMENT ON COLUMN mcp_tool_invocations.completed_at IS
    'ADR-0086: finalize instant; NULL while pending.';
COMMENT ON COLUMN mcp_tool_invocations.trace_id IS
    'ADR-0086: HttpContext.TraceIdentifier, correlating this row with the application log + client response.';
