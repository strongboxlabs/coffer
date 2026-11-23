using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The ledger's reporting currency: the one currency every figure that combines accounts
/// is shown in, converted (ADR-0114 D7). It is <c>ledgers.currency_code</c>, chosen by
/// the ledger's owner; nothing derives it from the accounts any more.
/// </summary>
/// <remarks>
/// <para>Migration 280 set each existing ledger's column from the rule this class used to
/// apply (the first code in ordinal order among net worth's accounts, else
/// <see cref="Fallback"/>), so a ledger reads the same code after the upgrade as before.
/// Since then a combined figure converts into it, and none is "mixed".</para>
/// <para><see cref="Of"/> survives for readers that name the one currency a set of
/// accounts agrees on (the holdings-currency check, a security's holders), which is not a
/// reporting currency and converts nothing.</para>
/// </remarks>
public static class DisplayCurrency
{
    /// <summary>The code a ledger created without one is in: the column's default.</summary>
    public const string Fallback = "USD";

    /// <summary>Net worth's account set: non-category, and not a holdings sibling,
    /// whose value folds into its brokerage.</summary>
    public static IQueryable<AccountRow> NetWorthAccounts(AppDbContext db, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && a.AccountType != "category"
                        && !db.Accounts.Any(o => o.LedgerId == ledgerId && o.HoldingsAccountId == a.Id));
    }

    /// <summary>The first of <paramref name="codes"/> in ordinal order, and whether they
    /// span more than one: the currency a set of accounts agrees on. Not a reporting
    /// currency.</summary>
    public static (string Code, bool Mixed) Of(IEnumerable<string> codes)
    {
        var distinct = codes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return (distinct.Count > 0 ? distinct[0] : Fallback, distinct.Count > 1);
    }

    /// <summary>The ledger's reporting currency (<c>ledgers.currency_code</c>).</summary>
    public static async Task<string> ForLedgerAsync(
        AppDbContext db, Guid ledgerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Ledgers.AsNoTracking()
                   .Where(l => l.Id == ledgerId)
                   .Select(l => l.CurrencyCode)
                   .FirstOrDefaultAsync(cancellationToken)
                   .ConfigureAwait(false)
               ?? Fallback;
    }
}
