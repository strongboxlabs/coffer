using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Coffer.Api.Auth;

namespace Coffer.Api.Mcp;

/// <summary>
/// Per-ledger write authorization for MCP write tools (ADR-0083 D2). A CallTool filter
/// that, for a write tool (<see cref="McpWriteTools.ToolNames"/>), reads its
/// <c>ledgerId</c> argument, resolves the caller's ledger role via
/// <see cref="LedgerAuthorizer"/> and throws unless they are owner/editor — so a
/// viewer's MCP write is refused with a clear error instead of the silent 0-row no-op
/// RLS produces on a blocked UPDATE/DELETE. Role-aware RLS (migration 174) is the DB
/// backstop; this is the friendly primary check, at parity with the REST
/// <c>RequireLedgerAccess</c> filter. One central filter covers every write tool
/// (present + future) — no per-tool wiring. Registered INSIDE the audit filter so a
/// blocked attempt is still recorded.
/// </summary>
/// <remarks>
/// Fails closed (FU-093). It used to pass any write call whose <c>ledgerId</c> was missing,
/// misnamed or not a GUID, on the reasoning that no write tool lacked one — true, and
/// enforced by nothing, so a future tool taking <c>ledger</c> or <c>id</c> would have been
/// checked by RLS alone. A write with no readable <c>ledgerId</c> is now refused, and
/// <c>McpWriteGuardTests</c> holds every write tool to taking one.
/// </remarks>
public static class McpLedgerWriteAuthFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create() =>
        next => async (context, cancellationToken) =>
        {
            await AuthorizeAsync(
                    context.Params?.Name, context.Params?.Arguments,
                    context.Services?.GetService<LedgerAuthorizer>(), cancellationToken)
                .ConfigureAwait(false);
            return await next(context, cancellationToken).ConfigureAwait(false);
        };

    /// <summary>
    /// Throws unless <paramref name="toolName"/> is not a write tool, or it carries a
    /// <c>ledgerId</c> the caller may write to.
    /// </summary>
    internal static async Task AuthorizeAsync(
        string? toolName, IDictionary<string, JsonElement>? arguments, LedgerAuthorizer? authorizer,
        CancellationToken cancellationToken)
    {
        if (toolName is null || !McpWriteTools.ToolNames.Contains(toolName)) return;

        var ledgerId = TryReadLedgerId(arguments)
            ?? throw new InvalidOperationException(
                "A write needs the ledger it writes to: pass ledgerId (a GUID from list_ledgers).");
        // Without the authorizer nothing can say this caller may write; refuse rather
        // than leave it to RLS.
        if (authorizer is null
            || !await authorizer.CanWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "This ledger is read-only for you (viewer). Writing requires editor or owner access.");
        }
    }

    private static Guid? TryReadLedgerId(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue("ledgerId", out var element))
            return null;
        return element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var id)
            ? id
            : null;
    }
}
