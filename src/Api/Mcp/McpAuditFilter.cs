using System.Diagnostics;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Coffer.Api.Auth;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Mcp;

/// <summary>
/// The MCP CallTool interceptor (ADR-0081 D3 audit + ADR-0086 observability): a
/// filter registered via <c>WithRequestFilters(f =&gt; f.AddCallToolFilter(...))</c>
/// that wraps every tool invocation. For WRITE tools it maintains the two-phase
/// <c>mcp_tool_invocations</c> audit (a <c>pending</c> row before the call,
/// finalized to <c>ok</c>/<c>error</c>/<c>cancelled</c> after). For EVERY tool it
/// emits leveled, correlated application logs — start, completion+duration,
/// error, and cancellation — so the MCP surface reaches the same observability as
/// the HTTP path (which <c>UseExceptionHandler</c> already covers).
/// </summary>
/// <remarks>
/// A single central filter is the whole-surface interceptor (a new write tool is
/// audited + logged automatically, no per-tool wiring). Auditing runs on
/// <see cref="CancellationToken.None"/> via <see cref="McpAuditRecorder"/> so a
/// client cancel/timeout cannot drop the record (the original defect); an auditing
/// failure is logged at Error, never silently swallowed, and never breaks the call.
/// The MCP SDK otherwise converts a thrown tool exception into an <c>IsError</c>
/// result before it reaches <c>UseExceptionHandler</c>, so this filter is the only
/// place a tool failure gets into the application log.
/// </remarks>
public static class McpAuditFilter
{
    private const string LoggerCategory = "Coffer.Api.Mcp.ToolCalls";

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create() =>
        next => async (context, cancellationToken) =>
        {
            var toolName = context.Params?.Name ?? "(unknown)";
            var isWrite = McpWriteTools.ToolNames.Contains(toolName);

            var services = context.Services;
            var logger = services?.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);
            var userId = services?.GetService<ICurrentUserAccessor>()?.UserId;
            var traceId = services?.GetService<IHttpContextAccessor>()?.HttpContext?.TraceIdentifier;
            var ledgerId = McpAuditRecorder.SummarizeArguments(context.Params?.Arguments).LedgerId;

            // Phase 1 (write tools only): a pending attempt row BEFORE the tool runs,
            // so a hang/cancel/crash still leaves a row. Never breaks the call.
            Guid? auditId = null;
            if (isWrite && userId is { } uid && uid != Guid.Empty && services is not null)
                auditId = await TryRecordAttemptAsync(
                    services, logger, uid, toolName, context.Params?.Arguments, traceId).ConfigureAwait(false);

            using var scope = logger?.BeginScope(new Dictionary<string, object?>
            {
                ["mcpTool"] = toolName,
                ["ledgerId"] = ledgerId,
            });

            var sw = Stopwatch.StartNew();
            logger?.LogDebug("MCP tool {Tool} starting", toolName);

            CallToolResult result;
            try
            {
                result = await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await FinalizeSafeAsync(services, logger, auditId, InvocationStatus.Cancelled, "cancelled")
                    .ConfigureAwait(false);
                logger?.LogWarning("MCP tool {Tool} cancelled after {ElapsedMs}ms", toolName, sw.ElapsedMilliseconds);
                throw;
            }
            catch (Exception ex)
            {
                // Log the exception HERE, before the SDK swallows it into an IsError result.
                // Record ex.ToString() (bounded by the recorder), not just ex.Message — a
                // DbUpdateException's Message is the useless "see the inner exception"; the
                // inner (e.g. the violated constraint) is what makes the audit diagnostic.
                await FinalizeSafeAsync(services, logger, auditId, InvocationStatus.Error, ex.ToString())
                    .ConfigureAwait(false);
                logger?.LogError(ex, "MCP tool {Tool} threw after {ElapsedMs}ms", toolName, sw.ElapsedMilliseconds);
                throw;
            }

            var isError = result.IsError ?? false;
            await FinalizeSafeAsync(services, logger, auditId,
                isError ? InvocationStatus.Error : InvocationStatus.Ok, Summarize(result, logger)).ConfigureAwait(false);

            if (isError)
                logger?.LogWarning("MCP tool {Tool} returned an error in {ElapsedMs}ms", toolName, sw.ElapsedMilliseconds);
            else
                logger?.Log(isWrite ? LogLevel.Information : LogLevel.Debug,
                    "MCP tool {Tool} completed in {ElapsedMs}ms", toolName, sw.ElapsedMilliseconds);

            return result;
        };

    private static async Task<Guid?> TryRecordAttemptAsync(
        IServiceProvider services, ILogger? logger, Guid userId, string toolName,
        IDictionary<string, JsonElement>? arguments, string? traceId)
    {
        try
        {
            var recorder = services.GetRequiredService<McpAuditRecorder>();
            return await recorder.RecordAttemptAsync(userId, toolName, arguments, traceId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Auditing must never break a tool call — but the failure is loud, not swallowed.
            logger?.LogError(ex, "MCP write-audit attempt-record failed for {Tool}", toolName);
            return null;
        }
    }

    private static async Task FinalizeSafeAsync(
        IServiceProvider? services, ILogger? logger, Guid? auditId, string status, string? result)
    {
        if (services is null || auditId is not { } id) return;
        try
        {
            var recorder = services.GetRequiredService<McpAuditRecorder>();
            await recorder.FinalizeAsync(id, status, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "MCP write-audit finalize failed for {AuditId} (status {Status})", id, status);
        }
    }

    private static string? Summarize(CallToolResult result, ILogger? logger)
    {
        try { return JsonSerializer.Serialize(result.Content); }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "MCP write-audit: failed to serialize tool result summary; recording no result");
            return null;
        }
    }
}
