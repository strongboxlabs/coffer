using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Coffer.Api.Auth;

namespace Coffer.Api.Mcp;

/// <summary>
/// Per-ledger write authorization for MCP write tools (ADR-0083 D2). A CallTool filter
/// that, for a write tool (<see cref="McpWriteTools.ToolNames"/>) carrying a
/// <c>ledgerId</c> argument, resolves the caller's ledger role via
/// <see cref="LedgerAuthorizer"/> and throws unless they are owner/editor — so a
/// viewer's MCP write is refused with a clear error instead of the silent 0-row no-op
/// RLS produces on a blocked UPDATE/DELETE. Role-aware RLS (migration 174) is the DB
/// backstop; this is the friendly primary check, at parity with the REST
/// <c>RequireLedgerAccess</c> filter. One central filter covers every write tool
/// (present + future) — no per-tool wiring. Registered INSIDE the audit filter so a
/// blocked attempt is still recorded.
/// </summary>
public static class McpLedgerWriteAuthFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create() =>
        next => async (context, cancellationToken) =>
        {
            var toolName = context.Params?.Name;
            if (toolName is null || !McpWriteTools.ToolNames.Contains(toolName))
                return await next(context, cancellationToken).ConfigureAwait(false);

            var ledgerId = TryReadLedgerId(context.Params?.Arguments);
            var authorizer = context.Services?.GetService<LedgerAuthorizer>();

            // A write tool scoped to a ledger must pass the owner/editor check. A write
            // tool with no ledgerId argument (none today) falls through to McpWriteGuard
            // + the RLS backstop.
            if (ledgerId is not null && authorizer is not null
                && !await authorizer.CanWriteAsync(ledgerId.Value, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "This ledger is read-only for you (viewer). Writing requires editor or owner access.");
            }

            return await next(context, cancellationToken).ConfigureAwait(false);
        };

    private static Guid? TryReadLedgerId(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue("ledgerId", out var element))
            return null;
        return element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var id)
            ? id
            : null;
    }
}
