using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Read-only Portfolio View for one investment (brokerage) account.
/// Resolves the brokerage's Holdings sibling (per ADR-0019), pulls the
/// per-(account, security) positions held on the caller's today with the price a
/// current value is computed at (the latest close on or before that day, else the
/// last trade's price), and computes per-position + summary metrics in C#
/// (the per-row arithmetic is straightforward and benefits from .NET's
/// decimal semantics over Postgres NUMERIC casting).
/// </summary>
public sealed class HoldingsRepository
{
    private readonly AppDbContext _db;
    private readonly AccountBalancesRepository _balances;
    private readonly InvestmentReportingRepository _investmentReporting;

    public HoldingsRepository(
        AppDbContext db,
        AccountBalancesRepository balances,
        InvestmentReportingRepository investmentReporting)
    {
        _db = db;
        _balances = balances;
        _investmentReporting = investmentReporting;
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
    /// carries a populated <see cref="Result.View"/>. <paramref name="today"/> is the
    /// caller's local date, as the register takes it (<see cref="ScheduledBoundary"/>);
    /// null for the register's fallback.
    /// </summary>
    public async Task<Result> GetByBrokerageAsync(
        Guid ledgerId,
        Guid brokerageAccountId,
        DateOnly? today,
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
        // register's latest balance_after on the brokerage cash account among the
        // rows it does not call Scheduled, one definition reused everywhere.
        // Investment positions live on the Holdings sibling, valued separately below.
        var cashBalance = await _balances
            .GetCurrentBalanceAsync(ledgerId, brokerage.Id, today, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

        // Positions held on the same day, each with the price a current value is
        // computed at: the same read, and the same valuation, the dashboard and the
        // sidebar value this brokerage with (InvestmentReportingRepository), so the
        // card's total is the figure they show. Not the holdings projection, which
        // counts trades and splits the register calls Scheduled. Zero-quantity
        // positions are not returned: the importer leaves a row with quantity=0
        // after a position is fully sold, and surfacing those would clutter the table
        // with empty rows.
        var held = await _investmentReporting
            .CurrentPositionsAsync(ledgerId, [holdingsAccountId], today, cancellationToken)
            .ConfigureAwait(false);

        var securityIds = held.Select(h => h.SecurityId).Distinct().ToList();
        var securities = await _db.Securities.AsNoTracking()
            .Where(s => securityIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Ticker, s.Name, s.AssetClass })
            .ToDictionaryAsync(s => s.Id, cancellationToken)
            .ConfigureAwait(false);

        // An inner join, as the read this replaces was: a position whose security
        // row is gone is not shown.
        var rows = held
            .Where(h => securities.ContainsKey(h.SecurityId))
            .Select(h => new
            {
                h.SecurityId,
                securities[h.SecurityId].Ticker,
                SecurityName = securities[h.SecurityId].Name,
                securities[h.SecurityId].AssetClass,
                h.Quantity,
                h.CostBasis,
                h.Price,
                h.PriceDate,
                // Null when unpriced: the value is then the cost basis, in the account's.
                PriceCurrency = h.ValueCurrency,
            })
            .ToList();

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

        // Per-position metrics. The price is the latest close dated on or before
        // the day, else the security's last trade price, read with the positions.
        // CurrentValue and derivatives are null only when the security has neither;
        // PortfolioValue still credits cost_basis
        // for those positions so the summary remains meaningful (better
        // "treats unknown as carry-flat" than "treats unknown as zero").
        // A position priced in another currency than the account's converts into it at the
        // latest rate, a current value (ADR-0114 D7); its cost basis is already in the
        // account's, as its trades are. Every rate in one load, and none at all when every
        // position is priced in the account's currency.
        var account = brokerage.CurrencyCode;
        bool Foreign(string? priceCurrency) => priceCurrency is not null && priceCurrency != account;
        var lookup = _investmentReporting.Rates;
        var needs = lookup.NewNeeds();
        foreach (var r in rows.Where(r => Foreign(r.PriceCurrency))) needs.AddLatest(r.PriceCurrency!, account);
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);

        var positions = new List<PositionDto>(rows.Count);
        decimal totalPortfolioValue = 0m;
        decimal totalCostBasis = 0m;
        var missing = new List<RateUnavailable>();
        foreach (var r in rows)
        {
            var hasPrice = r.Price is not null;
            decimal? currentPrice = r.Price;
            DateOnly? priceAsOf = r.PriceDate;
            decimal? currentValue = r.Price is { } px
                ? ReportingScale.MarketValue(r.Quantity, px)
                : null;
            var costPerShare = ReportingScale.PerShare(r.CostBasis, r.Quantity);

            // The value the gain and the summary read, in the account's currency: the
            // position's own for one priced in it, else converted, null when its rate is
            // missing.
            var foreign = Foreign(r.PriceCurrency);
            Converted? converted = foreign
                ? rates.NewTotal(account).AddLatest(currentValue!.Value, r.PriceCurrency!).Result
                : null;
            decimal? inAccount = foreign ? converted!.Value.Amount : currentValue;
            decimal? unrealized  = hasPrice ? inAccount - r.CostBasis : null;
            decimal? percent     = hasPrice && unrealized is { } u
                ? ReportingScale.PercentOrNull(u, r.CostBasis)
                : null;

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
                PercentChange: percent,
                PriceCurrencyCode: foreign ? r.PriceCurrency : null,
                AccountValue: inAccount is { } v && foreign ? v : null,
                RateUnavailable: converted is { Unavailable.Count: > 0 } c ? c.Unavailable : null));

            if (converted is { Unavailable.Count: > 0 } why)
                foreach (var w in why.Unavailable) if (!missing.Contains(w)) missing.Add(w);
            totalPortfolioValue += inAccount ?? (foreign ? 0m : r.CostBasis);
            totalCostBasis      += r.CostBasis;
        }

        // Fails closed (ADR-0114 D9): a position whose rate is missing leaves the value,
        // and every figure built on it, null and named, never a partial sum.
        decimal? portfolioValue = missing.Count > 0 ? null : totalPortfolioValue;
        decimal? summaryUnrealized = portfolioValue - totalCostBasis;
        decimal? summaryPercent = summaryUnrealized is { } gain
            ? ReportingScale.Percent(gain, totalCostBasis)
            : null;

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
                PortfolioValue: portfolioValue,
                CostBasis: totalCostBasis,
                UnrealizedGain: summaryUnrealized,
                PercentChange: summaryPercent,
                CashBalance: cashBalance,
                Total: portfolioValue + cashBalance,
                RateUnavailable: missing.Count > 0 ? missing : null),
            Positions: positions));
    }
}
