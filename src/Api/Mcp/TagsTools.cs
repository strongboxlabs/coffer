using System.ComponentModel;

using ModelContextProtocol.Server;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Mcp;

/// <summary>
/// MCP tools over tags (ADR-0077). Read-only here; the write path
/// (<c>set_transaction_tags</c>, the bulk tag replace-set) lives in
/// <see cref="McpWriteTools"/>, gated by <see cref="McpWriteGuard"/> (the
/// <c>coffer.write</c> scope + the writes kill-switch, ADR-0081). RLS scopes every
/// read to the bearer's user.
/// </summary>
[McpServerToolType]
public static class TagsTools
{
    [McpServerTool(Name = "list_tags"), Description(
        "List a ledger's tags with usage counts: id, name, color, and usageCount (the " +
        "number of transactions carrying the tag; 0 = unused). Tags are freeform labels " +
        "orthogonal to the single category — a transaction can carry several (e.g. " +
        "'reimbursable', 'tax-deductible', 'vacation-2026'). Resolve a tagId here to " +
        "filter or group other queries by tag. Use list_ledgers first to resolve ledgerId.")]
    public static async Task<IReadOnlyList<TagDto>> ListTags(
        TagsRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        CancellationToken cancellationToken = default) =>
        await repository.ListWithUsageAsync(ledgerId, cancellationToken).ConfigureAwait(false);
}
