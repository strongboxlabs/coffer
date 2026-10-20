namespace Coffer.Api.Contracts;

/// <summary>
/// Public DTO for one user-curated sidebar tab (migration 033).
/// Returned by <c>GET /api/ledgers/{ledgerId}/account-groups</c>;
/// drives the sidebar's tab strip. The implicit "All" tab is not a
/// row — the SPA renders it client-side.
/// </summary>
/// <param name="Id">Group id (UUID).</param>
/// <param name="Name">Display name shown in the tab strip.</param>
/// <param name="SortOrder">Ascending render order in the tab strip.</param>
/// <param name="MemberAccountIds">Account ids in this group, scoped
/// to the calling user's ledger access. Empty for an unpopulated
/// group (rendered with the "no accounts in this tab" hint).</param>
public sealed record AccountGroupSummary(
    Guid Id,
    string Name,
    int SortOrder,
    IReadOnlyList<Guid> MemberAccountIds);

/// <summary>
/// Request body for
/// <c>POST /api/ledgers/{ledgerId}/account-groups</c> — create a new
/// sidebar tab.
/// </summary>
public sealed class CreateAccountGroupRequest
{
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Request body for
/// <c>PATCH /api/ledgers/{ledgerId}/account-groups/{groupId}</c>.
/// Rename only at v1 — reorder is a follow-up (the schema's
/// <c>sort_order</c> column is reserved for that path).
/// </summary>
public sealed class PatchAccountGroupRequest
{
    public string? Name { get; init; }
}
