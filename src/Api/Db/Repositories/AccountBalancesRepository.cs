using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The single read path for "an account's current balance" (ADR-0056 slice 1).
/// Backed by <c>account_balances_as_of_now</c> (migration 259): the register's latest
/// <c>balance_after</c> per account among the rows the register does not call
/// Scheduled, opening-balance fallback. Every consumer — the dashboard overview, the
/// sidebar and MCP balances, HoldingsRepository's brokerage-cash read — goes through
/// here so there is exactly one definition of "balance". The managed loan split is not
/// a current balance: it is priced as of the payment's own date (ADR-0078, 2026-10-21).
/// </summary>
/// <remarks>
/// The balance is the brokerage's <em>cash</em> side for an investment account
/// (positions live on the Holdings sibling and are valued separately). Callers
/// that need market value add it on top.
/// <para>
/// The boundary is the register's (<see cref="ScheduledBoundary"/>): a row dated
/// after the caller's local today is Scheduled there and left out here, and counts
/// the moment the caller's date reaches it, with nothing to refresh. A caller that
/// passes no date gets the register's fallback, the server clock's UTC date. The
/// register still lists such a row with the running balance it will leave.
/// </para>
/// </remarks>
public sealed class AccountBalancesRepository
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public AccountBalancesRepository(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Current balance for one account, or <c>null</c> when the account is not
    /// in the ledger (or not visible under RLS). <paramref name="today"/> is the
    /// caller's local date, as the register takes it.
    /// </summary>
    public async Task<decimal?> GetCurrentBalanceAsync(
        Guid ledgerId, Guid accountId, DateOnly? today, CancellationToken cancellationToken = default)
    {
        var rows = await _db.AccountBalancesAsOfNow(ledgerId, ScheduledBoundary.ScheduledFrom(today, _clock))
            .AsNoTracking()
            .Where(b => b.AccountId == accountId)
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
    /// net-worth path passes <c>false</c>. <paramref name="today"/> is the caller's
    /// local date, as the register takes it.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> GetCurrentBalancesAsync(
        Guid ledgerId, bool activeOnly, DateOnly? today, CancellationToken cancellationToken = default)
    {
        var rows = await _db.AccountBalancesAsOfNow(ledgerId, ScheduledBoundary.ScheduledFrom(today, _clock))
            .AsNoTracking()
            .Where(b => !activeOnly || b.IsActive)
            .Select(b => new { b.AccountId, b.Balance })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.AccountId, r => r.Balance);
    }
}
