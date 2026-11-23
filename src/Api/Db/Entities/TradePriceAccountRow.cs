namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result of <c>fn_trade_price_accounts(ledger, security_ids, days)</c>
/// (migration 274): one account whose currency the trade-price rule can read for one of
/// the given (security, day) pairs, whichever execution of the day it picks.
/// </summary>
/// <remarks>What a writer of a trade price, or of a currency the rule reads, locks before
/// it reads (<see cref="AppDbContext.LockTradePriceAccounts"/>).</remarks>
internal sealed class TradePriceAccountRow
{
    public Guid AccountId { get; init; }
}
