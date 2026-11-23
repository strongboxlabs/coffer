namespace Coffer.Api.Db.Entities;

/// <summary>
/// One row of <c>account_balance_as_of_instants</c> (migration 201): an account's
/// cash balance at one of many requested instants.
/// <para>
/// The batched form exists because a TWR boundary valuation needs cash as well as
/// holdings, and calling the per-instant function once per boundary costs one round
/// trip per boundary, every one over the same balance rows. Migration 200 batches the
/// holdings half; this batches the cash half.
/// </para>
/// </summary>
internal sealed class AccountBalanceAsOfInstantRow
{
    public DateTime AsOf { get; init; }
    public Guid AccountId { get; init; }
    public decimal Balance { get; init; }
}
