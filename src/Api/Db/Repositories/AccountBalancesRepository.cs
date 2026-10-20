using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The single read path for "an account's current balance" (ADR-0056 slice 1).
/// Backed by the <c>account_current_balances</c> view (migration 133): the
/// register's latest <c>balance_after</c> per account, opening-balance fallback.
/// Every consumer — the dashboard overview, HoldingsRepository's brokerage-cash
/// read — goes through here so there is exactly one definition of "balance".
/// </summary>
/// <remarks>
/// The balance is the brokerage's <em>cash</em> side for an investment account
/// (positions live on the Holdings sibling and are valued separately). Callers
/// that need market value add it on top.
/// </remarks>
public sealed class AccountBalancesRepository
{
    private readonly AppDbContext _db;

    public AccountBalancesRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Current balance for one account, or <c>null</c> when the account is not
    /// in the ledger (or not visible under RLS).
    /// </summary>
    public async Task<decimal?> GetCurrentBalanceAsync(
        Guid ledgerId, Guid accountId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.AccountCurrentBalances.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId && b.AccountId == accountId)
            .Select(b => (decimal?)b.Balance)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// Current balance for every account in the ledger, keyed by account id.
    /// <paramref name="activeOnly"/> is a <b>catalog-listing</b> convenience only
    /// (hide closed accounts from a list) — pass <c>false</c> from any
    /// valuation/aggregate caller. Net worth must NOT gate on is_active
    /// (ADR-0085): a closed account's residual value is real. The default stays
    /// <c>true</c> for the list surfaces that opt into hiding; the Overview /
    /// net-worth path passes <c>false</c>.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> GetCurrentBalancesAsync(
        Guid ledgerId, bool activeOnly = true, CancellationToken cancellationToken = default)
    {
        var rows = await _db.AccountCurrentBalances.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId && (!activeOnly || b.IsActive))
            .Select(b => new { b.AccountId, b.Balance })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.AccountId, r => r.Balance);
    }
}
