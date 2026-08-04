-- =============================================================================
-- 170 — mcp_tool_invocations (ADR-0081 D3): per-call audit of MCP WRITE tools
-- =============================================================================
--
-- When MCP writes are enabled (ADR-0081 D1/D2), an AI client can mutate ledger
-- data through the write tools. This table is the audit trail: one row per write-
-- tool invocation — who (user), what (tool + bounded/serialized arguments), the
-- outcome (is_error + a bounded result summary), and when. Reads are NOT recorded
-- (high volume, low audit value); only the mutating surface is.
--
-- Written by McpAuditRecorder via the coffer_service role (like mcp_access_tokens,
-- migration 145): auditing must record reliably and is an oversight artifact, so
-- it does not depend on the caller's RLS write-check succeeding. The own-user RLS
-- policy below is defence-in-depth for any coffer_app access; the admin viewer
-- (ADR-0081 D5) reads across users via coffer_service.
--
-- Append-only in practice. ledger_id is a best-effort lift of the tool's ledgerId
-- argument for filtering; it carries NO foreign key so the audit row survives a
-- ledger delete (an audit you can erase by deleting the subject is no audit).
-- =============================================================================

CREATE TABLE mcp_tool_invocations (
    id          UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     UUID         NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    -- The MCP tool name, e.g. 'set_transaction_tags' (ADR-0068/0081 write tools).
    tool_name   TEXT         NOT NULL,
    -- Serialized, length-bounded JSON of the call arguments. Write-tool args carry
    -- ids + values (no credentials), so this is bounded, not deeply redacted.
    arguments   TEXT,
    -- Outcome: the tool reported an error (or threw) vs. succeeded.
    is_error    BOOLEAN      NOT NULL DEFAULT false,
    -- Bounded summary of the tool result / error message.
    result      TEXT,
    -- Best-effort: the ledgerId argument if the call carried one (no FK; see above).
    ledger_id   UUID,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT now(),

    CONSTRAINT ck_mcp_tool_invocations_tool_name CHECK (tool_name <> '')
);

CREATE INDEX idx_mcp_tool_invocations_user_time
    ON mcp_tool_invocations (user_id, created_at DESC);

COMMENT ON TABLE mcp_tool_invocations IS
    'ADR-0081 D3: per-call audit of MCP write-tool invocations (tool, user, bounded '
    'args, outcome, timestamp). Written via coffer_service; reads-are-not-audited.';

-- RLS — own-user only (the mcp_access_tokens pattern, migration 145). The recorder
-- writes as coffer_service (RLS-bypassing) with an explicit user_id, and the admin
-- viewer reads across users as coffer_service; this policy is defence-in-depth for
-- any coffer_app path.
ALTER TABLE mcp_tool_invocations ENABLE ROW LEVEL SECURITY;
ALTER TABLE mcp_tool_invocations FORCE  ROW LEVEL SECURITY;

DROP POLICY IF EXISTS mcp_tool_invocations_per_user ON mcp_tool_invocations;
CREATE POLICY mcp_tool_invocations_per_user ON mcp_tool_invocations
    FOR ALL
    TO coffer_app
    USING (user_id = current_app_user_id())
    WITH CHECK (user_id = current_app_user_id());

GRANT SELECT, INSERT ON mcp_tool_invocations TO coffer_app;
GRANT ALL ON mcp_tool_invocations TO coffer_service;
