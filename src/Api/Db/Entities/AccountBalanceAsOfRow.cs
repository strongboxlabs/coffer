namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the <c>account_balance_as_of</c> TVF (migration
/// 172) — the date-bounded twin of the mig-133 <c>account_current_balances</c>
/// view: an account's register balance as of an instant (the last
/// <c>balance_after</c> whose header posted on or before the instant, with
/// <c>opening_balance</c> as the fallback). Bound via <c>HasDbFunction</c> in
/// <see cref="AppDbContext"/>; the cash half of the as-of valuation feeder.
/// </summary>
internal sealed class AccountBalanceAsOfRow
{
    public Guid AccountId { get; init; }
    public decimal Balance { get; init; }
}
