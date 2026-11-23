using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// The currencies a conversion reads for one ledger: its reporting currency, and each
/// account's own (ADR-0114 D7).
/// </summary>
/// <remarks>
/// <para>A position sits on a brokerage's holdings account and is the brokerage's: its
/// figures are named in the brokerage's currency, the one a trade price is written in
/// (migration 274's <c>fn_trade_currency</c>: the first by code in ordinal order when
/// several brokerages name one holdings account, else the account's own). So a position
/// converts from its price's currency into <see cref="OfPositionsOn"/>, and the
/// brokerage then converts as a whole.</para>
/// <para>One read of the ledger's accounts, for every reader.</para>
/// </remarks>
internal sealed class LedgerCurrencies
{
    private readonly Dictionary<Guid, string> _own;
    private readonly Dictionary<Guid, string> _positions;

    private LedgerCurrencies(string reporting, Dictionary<Guid, string> own, Dictionary<Guid, string> positions)
    {
        Reporting = reporting;
        _own = own;
        _positions = positions;
    }

    /// <summary>The ledger's reporting currency (<c>ledgers.currency_code</c>).</summary>
    public string Reporting { get; }

    /// <summary>An account's own currency; the reporting currency for an account not in
    /// the ledger, which no figure of it reaches.</summary>
    public string Of(Guid accountId) => _own.GetValueOrDefault(accountId, Reporting);

    /// <summary>The currency a holdings account's positions are named in: its brokerage's,
    /// else its own.</summary>
    public string OfPositionsOn(Guid holdingsAccountId) =>
        _positions.TryGetValue(holdingsAccountId, out var c) ? c : Of(holdingsAccountId);

    public static async Task<LedgerCurrencies> LoadAsync(
        AppDbContext db, Guid ledgerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var reporting = await DisplayCurrency.ForLedgerAsync(db, ledgerId, cancellationToken).ConfigureAwait(false);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId)
            .Select(a => new { a.Id, a.CurrencyCode, a.HoldingsAccountId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var own = accounts.ToDictionary(a => a.Id, a => a.CurrencyCode);
        var positions = accounts
            .Where(a => a.HoldingsAccountId is not null)
            .GroupBy(a => a.HoldingsAccountId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(a => a.CurrencyCode).Order(StringComparer.Ordinal).First());
        return new LedgerCurrencies(reporting, own, positions);
    }
}

/// <summary>
/// A cost basis converted lot by lot at each lot's acquisition date, as a trade converts
/// at its trade date (ADR-0114 D7), for the positions whose currency differs from the
/// figure's. A position in the figure's own currency keeps the cost basis it reports.
/// </summary>
internal static class LotCosts
{
    /// <summary>Every lot open at <paramref name="asOf"/> on the holdings accounts given,
    /// in one statement; none read when there are none.</summary>
    public static async Task<ILookup<(Guid AccountId, Guid SecurityId), (DateOnly Acquired, decimal Cost)>> OpenAsync(
        AppDbContext db, Guid ledgerId, DateTime asOf, IReadOnlyCollection<Guid> holdingsAccountIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(holdingsAccountIds);
        if (holdingsAccountIds.Count == 0)
            return Array.Empty<(Guid, Guid, DateOnly, decimal)>()
                .ToLookup(x => (x.Item1, x.Item2), x => (x.Item3, x.Item4));

        var ids = holdingsAccountIds.ToArray();
        var lots = await db.HoldingsOpenLotsAsOf(ledgerId, asOf, ids)
            .AsNoTracking()
            .Select(l => new { l.AccountId, l.SecurityId, l.AcquiredAt, l.Cost })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return lots.ToLookup(
            l => (l.AccountId, l.SecurityId),
            l => (DateOnly.FromDateTime(l.AcquiredAt), l.Cost));
    }

    /// <summary>Declares the rate of each lot's acquisition day.</summary>
    public static void Declare(
        ExchangeRateNeeds needs, IEnumerable<(DateOnly Acquired, decimal Cost)> lots, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(needs);
        foreach (var lot in lots) needs.Add(from, to, lot.Acquired);
    }

    /// <summary>Adds each lot's cost, at its acquisition day's rate, to
    /// <paramref name="total"/>.</summary>
    public static void AddTo(
        ConvertedTotal total, IEnumerable<(DateOnly Acquired, decimal Cost)> lots, string from)
    {
        ArgumentNullException.ThrowIfNull(total);
        foreach (var lot in lots) total.Add(lot.Cost, from, lot.Acquired);
    }
}

/// <summary>Helpers for the ADR-0114 D9 answer on a DTO.</summary>
internal static class RateReasons
{
    /// <summary>No missing rate.</summary>
    public static IReadOnlyList<RateUnavailable> None { get; } = [];

    /// <summary>The distinct missing rates of several figures, in the order first met.</summary>
    public static IReadOnlyList<RateUnavailable> Of(params IEnumerable<RateUnavailable>[] lists) =>
        [.. lists.SelectMany(l => l).Distinct()];
}
