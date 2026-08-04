using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Admin read/clear over the MCP write audit (<c>mcp_tool_invocations</c>, ADR-0081
/// D3/D5). Uses the service role (BYPASSRLS) — the admin viewer deliberately spans
/// every user; the endpoints are <c>RequireAdmin</c> (deployment-global). Read-only
/// plus a bounded purge; the recorder is the only writer.
/// </summary>
public sealed class McpAuditRepository
{
    private readonly ServiceDbContextFactory _serviceFactory;

    public McpAuditRepository(ServiceDbContextFactory serviceFactory) => _serviceFactory = serviceFactory;

    /// <summary>Newest-first page of audit entries; <paramref name="before"/> is a
    /// created-at cursor for "load older".</summary>
    public async Task<IReadOnlyList<McpAuditEntryDto>> ListAsync(
        int take, DateTime? before, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 500);
        await using var db = _serviceFactory.Create();
        var query = db.McpToolInvocations.AsNoTracking();
        if (before is { } cursor)
            query = query.Where(r => r.CreatedAt < cursor);
        return await query
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .Join(db.Users.AsNoTracking(), r => r.UserId, u => u.Id,
                (r, u) => new McpAuditEntryDto(
                    r.Id, r.UserId, u.DisplayName, r.ToolName, r.LedgerId,
                    r.Arguments, r.Status, r.Result, r.CreatedAt, r.CompletedAt))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete all audit rows, or only those older than
    /// <paramref name="before"/>. Returns the number removed.</summary>
    public async Task<int> ClearAsync(DateTime? before, CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        IQueryable<McpToolInvocationRow> query = db.McpToolInvocations;
        if (before is { } cursor)
            query = query.Where(r => r.CreatedAt < cursor);
        return await query.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
