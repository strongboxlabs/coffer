namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF keyless projection of the <c>account_current_balances</c> view
/// (migration 133). One row per account carries its current balance — the
/// register's latest <c>balance_after</c>, opening-balance fallback — so the
/// overview and HoldingsRepository share one definition (ADR-0056 slice 1).
/// </summary>
internal sealed class AccountCurrentBalanceView
{
    public Guid AccountId { get; init; }
    public Guid LedgerId { get; init; }
    public bool IsActive { get; init; }
    public decimal Balance { get; init; }
}
