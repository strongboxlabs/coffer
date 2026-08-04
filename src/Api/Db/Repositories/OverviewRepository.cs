using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Computes the ledger overview aggregate (ADR-0056 slice 1): per-account
/// current balances grouped by type, net worth, and an investment roll-up.
/// </summary>
/// <remarks>
/// Account balances come from <see cref="AccountBalancesRepository"/> — the one
/// shared definition of "current balance" (the register's latest
/// <c>balance_after</c>, ADR-0034), never re-derived here. Investment accounts
/// add holdings market value (qty × latest <c>security_prices</c>, no-price
/// positions carried at cost basis). Liabilities are stored negative, so net
/// worth is a straight sum. LINQ/EF throughout (no raw SQL in the API).
/// </remarks>
public sealed class OverviewRepository
{
    // Net-worth-relevant account types — the shared definition (also drives the
    // assets/liabilities subtotals). Categories and the holdings-sibling shadow
    // accounts fall outside both sets (and the sibling is excluded explicitly).
    private static readonly string[] AssetTypes = AccountClassifier.AssetTypes;
    private static readonly string[] LiabilityTypes = AccountClassifier.LiabilityTypes;

    private readonly AppDbContext _db;
    private readonly AccountBalancesRepository _balances;
    private readonly InvestmentReportingRepository _investmentReporting;

    public OverviewRepository(
        AppDbContext db,
        AccountBalancesRepository balances,
        InvestmentReportingRepository investmentReporting)
    {
        _db = db;
        _balances = balances;
        _investmentReporting = investmentReporting;
    }

    public async Task<LedgerOverviewDto> GetAsync(
        Guid ledgerId, CancellationToken cancellationToken = default)
    {
        // 1) Net-worth-relevant accounts. The type filter excludes categories;
        //    the NOT EXISTS excludes holdings-sibling shadow accounts (same
        //    exclusion AccountsRepository uses) — their value folds into the
        //    owning brokerage below, never counted standalone. is_active is
        //    deliberately NOT filtered (ADR-0085): net worth reflects real value,
        //    so a closed account still holding value must count. is_active is a
        //    UI-surfacing flag, not a valuation gate; a closed-and-zeroed account
        //    contributes 0 regardless.
        var accounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && (AssetTypes.Contains(a.AccountType)
                            || LiabilityTypes.Contains(a.AccountType))
                        && !_db.Accounts.Any(o =>
                            o.LedgerId == ledgerId && o.HoldingsAccountId == a.Id))
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.AccountType,
                a.CurrencyCode,
                a.HoldingsAccountId,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 2) Current cash balance per account, from the shared balance source.
        //    activeOnly: false — net worth includes every real account regardless
        //    of is_active (ADR-0085); a closed account's residual value is real.
        var balanceByAccountId = await _balances
            .GetCurrentBalancesAsync(ledgerId, activeOnly: false, cancellationToken)
            .ConfigureAwait(false);

        // 3) Holdings market value per holdings-sibling account (the single
        //    shared definition, reused by net worth + accounts reporting), plus
        //    the portfolio cost basis for the roll-up. Portfolio value = the sum
        //    of those market values.
        var holdingsValueByAccount = await _investmentReporting
            .MarketValueByAccountAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        var portfolioValue = holdingsValueByAccount.Values.Sum();
        var portfolioCost = await _db.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId && h.Quantity != 0m)
            .SumAsync(h => h.CostBasis, cancellationToken)
            .ConfigureAwait(false);

        // 4) Per-account balance = cash (+ holdings value for investments).
        var enriched = accounts
            .Select(a =>
            {
                var balance = balanceByAccountId.GetValueOrDefault(a.Id);
                if (a.AccountType == "investment" && a.HoldingsAccountId is Guid sibling)
                {
                    balance += holdingsValueByAccount.GetValueOrDefault(sibling);
                }
                return new
                {
                    a.Id,
                    a.Name,
                    a.AccountType,
                    a.CurrencyCode,
                    Balance = balance,
                };
            })
            .ToList();

        // 5) Group by type with subtotals; totals; net worth (a straight sum,
        //    liabilities already negative).
        var groups = enriched
            .GroupBy(a => a.AccountType)
            .Select(g => new OverviewAccountGroupDto(
                g.Key,
                g.Sum(a => a.Balance),
                g.OrderBy(a => a.Name, StringComparer.Ordinal)
                    .Select(a => new OverviewAccountDto(
                        a.Id, a.Name, a.AccountType, a.CurrencyCode, a.Balance))
                    .ToList()))
            .OrderBy(g => TypeOrder(g.AccountType))
            .ToList();

        var totalAssets = enriched
            .Where(a => AssetTypes.Contains(a.AccountType))
            .Sum(a => a.Balance);
        var totalLiabilities = enriched
            .Where(a => LiabilityTypes.Contains(a.AccountType))
            .Sum(a => a.Balance);
        var investmentsValue = enriched
            .Where(a => a.AccountType == "investment")
            .Sum(a => a.Balance);

        var currencies = enriched.Select(a => a.CurrencyCode).Distinct().ToList();
        var displayCurrency = currencies.Count > 0 ? currencies[0] : "USD";

        var portfolio = new PortfolioRollupDto(
            Value: portfolioValue,
            CostBasis: portfolioCost,
            UnrealizedGain: portfolioValue - portfolioCost,
            PercentChange: portfolioCost != 0m
                ? (portfolioValue - portfolioCost) / portfolioCost * 100m
                : 0m);

        return new LedgerOverviewDto(
            NetWorth: totalAssets + totalLiabilities,
            TotalAssets: totalAssets,
            TotalLiabilities: totalLiabilities,
            InvestmentsValue: investmentsValue,
            CurrencyCode: displayCurrency,
            MixedCurrency: currencies.Count > 1,
            AccountGroups: groups,
            Portfolio: portfolio);
    }

    // Display order for the account-type groups (mirrors the old Hub order).
    private static int TypeOrder(string type) => type switch
    {
        "bank" => 0,
        "cash" => 1,
        "credit_card" => 2,
        "investment" => 3,
        "asset" => 4,
        "liability" => 5,
        "loan" => 6,
        _ => 99,
    };
}
