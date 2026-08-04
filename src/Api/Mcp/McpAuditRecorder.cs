using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Mcp;

/// <summary>
/// Records the <c>mcp_tool_invocations</c> audit for MCP write-tool calls
/// (ADR-0081 D3, made two-phase by ADR-0086). Persists via the service role
/// (<see cref="ServiceDbContextFactory"/>) with an explicit user id — like
/// <c>McpTokensRepository</c>, because auditing must record reliably and is an
/// oversight artifact, so it does not hinge on the caller's RLS write-check.
/// <see cref="McpAuditFilter"/> is the single caller (a CallTool filter around
/// every write-tool invocation).
/// </summary>
/// <remarks>
/// Two-phase (ADR-0086): <see cref="RecordAttemptAsync"/> writes a
/// <c>pending</c> row BEFORE the tool runs — so every committed change already
/// has a row — and <see cref="FinalizeAsync"/> transitions it to a terminal
/// state afterward. Both run on <see cref="CancellationToken.None"/>, decoupled
/// from the caller's cancellation, so a client timeout/cancel can never drop the
/// record (the original defect). A process crash between the two leaves a visible
/// <c>pending</c> row — a known unknown, never a silent loss.
/// </remarks>
public sealed class McpAuditRecorder
{
    /// <summary>Max stored length of the serialized arguments before truncation.</summary>
    public const int MaxArgumentsLength = 4000;

    /// <summary>Max stored length of the result summary before truncation.</summary>
    public const int MaxResultLength = 2000;

    private readonly ServiceDbContextFactory _serviceFactory;

    public McpAuditRecorder(ServiceDbContextFactory serviceFactory) => _serviceFactory = serviceFactory;

    /// <summary>
    /// Phase 1: append a <c>pending</c> attempt row before the tool executes and
    /// return its id. Written on <see cref="CancellationToken.None"/> so a
    /// cancelled/timed-out request still records that the call was attempted.
    /// </summary>
    public async Task<Guid> RecordAttemptAsync(
        Guid userId,
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        string? traceId)
    {
        var (argumentsJson, ledgerId) = SummarizeArguments(arguments);
        var id = Guid.NewGuid();

        await using var db = _serviceFactory.Create();
        db.McpToolInvocations.Add(new McpToolInvocationRow
        {
            Id = id,
            UserId = userId,
            ToolName = toolName,
            Arguments = argumentsJson,
            LedgerId = ledgerId,
            TraceId = traceId,
            Status = InvocationStatus.Pending,
            // CreatedAt is DB-assigned (default now()); CompletedAt is null while pending.
        });
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Phase 2: transition a pending row to a terminal <paramref name="status"/>
    /// (<c>ok</c> / <c>error</c> / <c>cancelled</c>) with a bounded result summary
    /// and completion instant. Runs on <see cref="CancellationToken.None"/> — the
    /// outcome must be recorded even when the caller's token is already cancelled.
    /// </summary>
    public async Task FinalizeAsync(Guid id, string status, string? result)
    {
        var completedAt = DateTime.UtcNow;
        var boundedResult = Bound(result, MaxResultLength);

        await using var db = _serviceFactory.Create();
        await db.McpToolInvocations
            .Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.Result, boundedResult)
                .SetProperty(x => x.CompletedAt, completedAt),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Serialize the call arguments to a bounded JSON string, and best-effort lift the
    /// <c>ledgerId</c> argument (a string GUID) so the admin viewer can filter by
    /// ledger. Pure — the unit-tested core. Write-tool arguments are ids/values with
    /// no credentials, so this bounds length rather than deeply redacting.
    /// </summary>
    public static (string? Json, Guid? LedgerId) SummarizeArguments(
        IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return (null, null);

        Guid? ledgerId = null;
        if (arguments.TryGetValue("ledgerId", out var raw)
            && raw.ValueKind == JsonValueKind.String
            && Guid.TryParse(raw.GetString(), out var parsed))
            ledgerId = parsed;

        return (Bound(JsonSerializer.Serialize(arguments), MaxArgumentsLength), ledgerId);
    }

    private static string? Bound(string? value, int max) =>
        value is null ? null
        : value.Length <= max ? value
        : string.Concat(value.AsSpan(0, max), "…[truncated]");
}
