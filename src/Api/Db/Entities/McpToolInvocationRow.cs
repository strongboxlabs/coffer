namespace Coffer.Api.Db.Entities;

/// <summary>
/// A row in <c>mcp_tool_invocations</c> (ADR-0081 D3): the audit record of one MCP
/// write-tool call. Written by <c>McpAuditRecorder</c> via the service role. Reads
/// are not audited — only the mutating surface.
/// </summary>
/// <remarks>
/// Two-phase (ADR-0086): a <c>pending</c> row is written before the tool runs and
/// finalized to a terminal <see cref="Status"/> afterward, so a cancelled / hung
/// call is never silently unrecorded. <see cref="Status"/> is the sole outcome
/// field (the older redundant <c>is_error</c> boolean was retired in migration 184).
/// </remarks>
public sealed class McpToolInvocationRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ToolName { get; set; } = "";
    public string? Arguments { get; set; }
    public string? Result { get; set; }
    public Guid? LedgerId { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Lifecycle state (ADR-0086): <c>pending</c> (attempt, pre-call) →
    /// <c>ok</c> | <c>error</c> | <c>cancelled</c> (terminal, finalized post-call).</summary>
    public string Status { get; set; } = InvocationStatus.Pending;

    /// <summary>Finalize instant; <c>null</c> while <see cref="Status"/> is pending.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary><c>HttpContext.TraceIdentifier</c>, correlating this row with the
    /// application log line and the client's ProblemDetails <c>traceId</c>.</summary>
    public string? TraceId { get; set; }
}

/// <summary>The terminal + initial states of an <see cref="McpToolInvocationRow"/>
/// (ADR-0086). Mirrors the <c>ck_mcp_tool_invocations_status</c> check.</summary>
public static class InvocationStatus
{
    public const string Pending = "pending";
    public const string Ok = "ok";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
}
