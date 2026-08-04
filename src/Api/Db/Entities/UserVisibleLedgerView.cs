namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF Core query type backing the <c>user_visible_ledgers</c> view
/// (added in migration 014). Internal to the API — the public DTO is
/// <c>LedgerSummary</c> in the Ledgers feature folder; repositories
/// project from this type to it so the EF model stays separable from
/// the API surface.
/// </summary>
internal sealed class UserVisibleLedgerView
{
    public Guid UserId { get; init; }
    public Guid LedgerId { get; init; }
    public string LedgerName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public DateTime GrantedAt { get; init; }
}
