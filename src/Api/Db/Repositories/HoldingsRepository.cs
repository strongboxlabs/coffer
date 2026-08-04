using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Read-only Portfolio View for one investment (brokerage) account.
/// Resolves the brokerage's Holdings sibling (per ADR-0019), pulls the
/// per-(account, security) position rows, joins the latest price snapshot
/// per security, and computes per-position + summary metrics in C# (the
/// per-row arithmetic is straightforward and benefits from .NET's
/// decimal semantics over Postgres NUMERIC casting).
/// </summary>
public sealed class HoldingsRepository
{
    private readonly AppDbContext _db;
    private readonly AccountBalancesRepository _balances;

    public HoldingsRepository(AppDbContext db, AccountBalancesRepository balances)
    {
        _db = db;
        _balances = balances;
    }

    public enum ResultKind
    {
        Ok,
        AccountNotInLedger,
        NotAnInvestmentAccount,
        NoHoldingsSibling,
    }

    public sealed record Result(ResultKind Kind, HoldingsViewDto? View);

    /// <summary>
    /// Build the Portfolio View for <paramref name="brokerageAccountId"/>
    /// in <paramref name="ledgerId"/>. Returns one of the
    /// <see cref="ResultKind"/> cases; only <see cref="ResultKind.Ok"/>
    /// carries a populated <see cref="Result.View"/>.
    /// </summary>
    public async Task<Result> GetByBrokerageAsync(
        Guid ledgerId,
        Guid brokerageAccountId,
        CancellationToken cancellationToken = default)
    {
        // Resolve the brokerage and its Holdings sibling link in one
        // round-trip. We need the type (must be 'investment') and the
        // currency + name to populate the response envelope.
        var brokerage = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == brokerageAccountId && a.LedgerId == ledgerId)
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.AccountType,
                a.CurrencyCode,
                a.HoldingsAccountId,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (brokerage is null)
            return new Result(ResultKind.AccountNotInLedger, null);
        if (brokerage.AccountType != "investment")
            return new Result(ResultKind.NotAnInvestmentAccount, null);
        if (brokerage.HoldingsAccountId is null)
            return new Result(ResultKind.NoHoldingsSibling, null);

        var holdingsAccountId = brokerage.HoldingsAccountId.Value;

        // Cash balance from the shared balance source (ADR-0056 slice 1) — the
        // register's latest balance_after on the brokerage cash account, one
        // definition reused everywhere. Investment positions live on the
        // Holdings sibling, valued separately below.
        var cashBalance = await _balances
            .GetCurrentBalanceAsync(ledgerId, brokerage.Id, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

        // Holdings + security metadata in one round-trip via a join.
        // Filtering zero-quantity rows: the importer leaves a row with
        // quantity=0 after a position is fully sold; surfacing those in
        // the Portfolio View would clutter the table with empty rows.
        var rows = await _db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == holdingsAccountId && h.Quantity != 0m)
            .Join(
                _db.Securities.AsNoTracking(),
                h => h.SecurityId,
                s => s.Id,
                (h, s) => new
                {
                    h.SecurityId,
                    s.Ticker,
                    SecurityName = s.Name,
                    s.AssetClass,
                    h.Quantity,
                    h.CostBasis,
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            // No positions — empty Portfolio View, but cash side is still
            // meaningful (the brokerage may hold pre-purchase cash).
            return new Result(ResultKind.Ok, new HoldingsViewDto(
                AccountId: brokerage.Id,
                AccountName: brokerage.Name,
                CurrencyCode: brokerage.CurrencyCode,
                Summary: new PortfolioSummaryDto(
                    PortfolioValue: 0m,
                    CostBasis: 0m,
                    UnrealizedGain: 0m,
                    PercentChange: 0m,
                    CashBalance: cashBalance,
                    Total: cashBalance),
                Positions: Array.Empty<PositionDto>()));
        }

        // Latest price per security touched by this account. Pulling all
        // matching prices and reducing in C# is a single round-trip; the
        // (security_id, price_date DESC) index on security_prices makes
        // the per-security MAX cheap, but EF's GroupBy-with-First in
        // LINQ-to-PostgreSQL is awkward — the C#-side reduction is
        // clearer and adequately fast for the position counts we see
        // (a large real-world export: hundreds of holdings across many
        // brokerages → a dozen-ish securities per call worst case).
        var securityIds = rows.Select(r => r.SecurityId).Distinct().ToList();
        var prices = await _db.SecurityPrices.AsNoTracking()
            .Where(p => securityIds.Contains(p.SecurityId))
            .Select(p => new { p.SecurityId, p.Price, p.PriceDate })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var latestBySecurity = prices
            .GroupBy(p => p.SecurityId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(p => p.PriceDate).First());

        // Per-position metrics. CurrentValue and derivatives are null
        // when no price exists; PortfolioValue still credits cost_basis
        // for those positions so the summary remains meaningful (better
        // "treats unknown as carry-flat" than "treats unknown as zero").
        var positions = new List<PositionDto>(rows.Count);
        decimal totalPortfolioValue = 0m;
        decimal totalCostBasis = 0m;
        foreach (var r in rows)
        {
            var hasPrice = latestBySecurity.TryGetValue(r.SecurityId, out var px);
            decimal? currentPrice = hasPrice ? px!.Price : null;
            DateOnly? priceAsOf = hasPrice ? px!.PriceDate : null;
            decimal? currentValue = hasPrice ? r.Quantity * px!.Price : null;
            decimal? unrealized  = hasPrice ? currentValue - r.CostBasis : null;
            decimal? percent     = hasPrice && r.CostBasis != 0m
                ? unrealized!.Value / r.CostBasis * 100m
                : null;
            var costPerShare = r.Quantity != 0m ? r.CostBasis / r.Quantity : 0m;

            positions.Add(new PositionDto(
                SecurityId: r.SecurityId,
                Ticker: r.Ticker,
                Name: r.SecurityName,
                AssetClass: r.AssetClass,
                Quantity: r.Quantity,
                CostBasis: r.CostBasis,
                CostPerShare: costPerShare,
                CurrentPrice: currentPrice,
                PriceAsOf: priceAsOf,
                CurrentValue: currentValue,
                UnrealizedGain: unrealized,
                PercentChange: percent));

            totalPortfolioValue += currentValue ?? r.CostBasis;
            totalCostBasis      += r.CostBasis;
        }

        var summaryUnrealized = totalPortfolioValue - totalCostBasis;
        var summaryPercent = totalCostBasis != 0m
            ? summaryUnrealized / totalCostBasis * 100m
            : 0m;

        // Stable ordering for the UI: ticker ascending, with no-ticker
        // securities sorting after tickered ones (rare; happens for
        // private/manual entries).
        positions.Sort((a, b) =>
        {
            if (a.Ticker is null && b.Ticker is null)
                return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            if (a.Ticker is null) return 1;
            if (b.Ticker is null) return -1;
            return string.Compare(a.Ticker, b.Ticker, StringComparison.Ordinal);
        });

        return new Result(ResultKind.Ok, new HoldingsViewDto(
            AccountId: brokerage.Id,
            AccountName: brokerage.Name,
            CurrencyCode: brokerage.CurrencyCode,
            Summary: new PortfolioSummaryDto(
                PortfolioValue: totalPortfolioValue,
                CostBasis: totalCostBasis,
                UnrealizedGain: summaryUnrealized,
                PercentChange: summaryPercent,
                CashBalance: cashBalance,
                Total: totalPortfolioValue + cashBalance),
            Positions: positions));
    }
}
