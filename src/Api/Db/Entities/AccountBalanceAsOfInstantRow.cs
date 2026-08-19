namespace Coffer.Api.Db.Entities;

/// <summary>
/// One row of <c>account_balance_as_of_instants</c> (migration 201): an account's
/// cash balance at one of many requested instants.
/// <para>
/// The batched form exists because a TWR boundary valuation needs cash as well as
/// holdings, and calling the per-instant function once per boundary meant 400 round
/// trips over the same balance rows. With migration 200 having batched the holdings
/// half, this was the remaining per-boundary cost.
/// </para>
/// </summary>
internal sealed class AccountBalanceAsOfInstantRow
{
    public DateTime AsOf { get; init; }
    public Guid AccountId { get; init; }
    public decimal Balance { get; init; }
}
