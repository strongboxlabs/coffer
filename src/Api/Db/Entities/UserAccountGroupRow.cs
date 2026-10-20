namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of one user-curated sidebar tab (migration 033).
/// Each row is a named group of accounts scoped to one (user, ledger).
/// The implicit "All" tab is not a row — the SPA renders it when no
/// group filter is in effect.
/// </summary>
public sealed class UserAccountGroupRow
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public Guid LedgerId { get; init; }
    /// <summary>Display name shown in the sidebar tab strip. Unique
    /// (case-insensitive) within (user, ledger) by index.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Ascending sidebar render order. Set on create / on
    /// reorder; tie-broken by created_at.</summary>
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; init; }
}
