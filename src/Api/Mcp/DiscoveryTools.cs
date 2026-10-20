using System.ComponentModel;

using ModelContextProtocol.Server;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Mcp;

/// <summary>
/// Discovery tools (ADR-0063 §D5): resolve the ids every reporting tool needs.
/// <c>list_ledgers</c> is the entry point — the model calls it first to turn a
/// name like "main ledger" into the GUID the other tools take. Scoped to the
/// bearer's user via the current-user accessor + RLS.
/// </summary>
[McpServerToolType]
public static class DiscoveryTools
{
    [McpServerTool(Name = "list_ledgers"), Description(
        "List the ledgers the authenticated user can access (id, name, role). Call this " +
        "first to resolve a ledger name to the ledgerId that reporting/investment tools take.")]
    public static async Task<IReadOnlyList<LedgerSummary>> ListLedgers(
        ICurrentUserAccessor currentUser,
        LedgersRepository repository,
        CancellationToken cancellationToken = default) =>
        await repository.GetVisibleAsync(currentUser.UserId, cancellationToken).ConfigureAwait(false);
}
