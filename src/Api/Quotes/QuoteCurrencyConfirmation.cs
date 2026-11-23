using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Quotes;

/// <summary>
/// The repair of the fetched prices stored before the quote-currency fix, each stamped USD
/// whatever its symbol trades in (ADR-0114 D5, amendment). The one writer of
/// <c>securities.quote_currency_confirmed_at</c> (migration 300).
/// </summary>
/// <remarks>
/// <para><b>Confirm.</b> When a Yahoo read names a security's currency,
/// <see cref="ConfirmAsync"/> stamps the security's <c>quote_currency_confirmed_at</c> and,
/// in the same transaction, re-stamps the currency of every <c>source = 'fetch'</c> row of
/// that security that carries another. For a symbol quoted in its major unit only
/// <c>currency_code</c> is written: price, high, low and volume stay as they are. For a
/// symbol quoted in a minor unit (<c>GBp</c>) those rows hold the minor unit's figures, so
/// they are converted the way a new fetch is stored: price, high and low divided by the
/// factor and rounded once to 4dp, the major code stamped, volume left as it is. A fetch row
/// already in the named currency, and a row from any other source (manual, trade, import,
/// simplefin), is not touched, since its currency is not Yahoo's to name.</para>
///
/// <para><b>Who calls it.</b> The quote refresh, with the currency of every fetch quote it
/// stored, and with the one-time pass's lookups (<see cref="PassCandidatesAsync"/>), and the
/// <c>quote_currency</c> consistency repair, with its own lookups.</para>
///
/// <para>No ledger write lock: nothing here recomputes a balance or a holding, which is
/// what the lock orders (migration 275). A plain transaction, or the caller's.</para>
/// </remarks>
internal static class QuoteCurrencyConfirmation
{
    /// <summary>What a confirmation wrote.</summary>
    /// <param name="CurrenciesConfirmed">Securities confirmed for the first time (their
    /// <c>quote_currency_confirmed_at</c> was NULL).</param>
    /// <param name="RowsRestamped">Fetch rows whose currency was re-stamped (and, for a
    /// minor-unit symbol, converted).</param>
    public sealed record Outcome(int CurrenciesConfirmed, int RowsRestamped)
    {
        public static readonly Outcome None = new(0, 0);
    }

    /// <summary>Decimal places <c>security_prices.price</c>, <c>high</c> and <c>low</c>
    /// store (NUMERIC(19,4), migration 155): a converted figure is rounded to it once.</summary>
    private const int PriceScale = 4;

    /// <summary>
    /// Stamp each named security's confirmation at <paramref name="confirmedAt"/> and re-stamp
    /// its fetch rows in another currency, in one transaction (the caller's when one is open).
    /// A security listed twice takes its last currency. A security not in the ledger is ignored.
    /// </summary>
    internal static async Task<Outcome> ConfirmAsync(
        AppDbContext db,
        Guid ledgerId,
        IEnumerable<QuoteCurrency> currencies,
        DateTime confirmedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(currencies);

        var bySecurity = new Dictionary<Guid, QuoteCurrency>();
        foreach (var c in currencies) bySecurity[c.SecurityId] = c;
        if (bySecurity.Count == 0) return Outcome.None;

        // timestamptz holds microseconds; rounded here, once, so the stored instant is the
        // one this method wrote.
        var at = new DateTime(confirmedAt.Ticks - confirmedAt.Ticks % 10, DateTimeKind.Utc);

        var owned = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            var ids = bySecurity.Keys.ToList();
            var firstTime = await db.Securities
                .Where(s => s.LedgerId == ledgerId && ids.Contains(s.Id) && s.QuoteCurrencyConfirmedAt == null)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            var restamped = 0;
            // Id order, so two confirmations of overlapping sets lock rows in one order.
            foreach (var securityId in ids.Order())
            {
                var named = bySecurity[securityId];
                var currency = named.CurrencyCode;
                var drifted = db.SecurityPrices
                    .Where(p => p.LedgerId == ledgerId
                                && p.SecurityId == securityId
                                && p.Source == PriceSource.Fetch
                                && p.CurrencyCode != currency);

                if (named.QuotedMinorUnitFactor == 1)
                {
                    // Quoted in its major unit: the figure was right, only the stamp was not.
                    restamped += await drifted
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.CurrencyCode, currency), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                // Quoted in a minor unit (GBp): a row stamped otherwise was stored before the
                // fix, as the quoted figure came, so it holds pence. Converted the way a new
                // fetch is stored: price, high and low divided by the factor and rounded once
                // at the column's scale, in C# with the rounding the fetch path uses; the
                // major code stamped; volume left as it is. A row already in the major unit is
                // not in this set and is not touched.
                var rows = await drifted.ToListAsync(cancellationToken).ConfigureAwait(false);
                var factor = (decimal)named.QuotedMinorUnitFactor;
                foreach (var row in rows)
                {
                    row.Price = decimal.Round(row.Price / factor, PriceScale);
                    row.High = row.High is { } high ? decimal.Round(high / factor, PriceScale) : null;
                    row.Low = row.Low is { } low ? decimal.Round(low / factor, PriceScale) : null;
                    row.CurrencyCode = currency;
                }
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                restamped += rows.Count;
            }

            await db.Securities
                .Where(s => s.LedgerId == ledgerId && ids.Contains(s.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.QuoteCurrencyConfirmedAt, at), cancellationToken)
                .ConfigureAwait(false);

            if (owned is not null) await owned.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new Outcome(firstTime, restamped);
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The securities the one-time pass asks about: those of the ledger with at least one
    /// fetch row and no confirmation, held or not, whose symbol (quote symbol, else ticker)
    /// is public (ADR-0054 D2), less <paramref name="alreadyAsked"/>. Ordered by id.
    /// </summary>
    /// <remarks>
    /// "Once" because a confirmed security drops out; a lookup that fails leaves it here
    /// for the next refresh.
    /// </remarks>
    internal static async Task<List<QuoteSecurityRef>> PassCandidatesAsync(
        AppDbContext db,
        Guid ledgerId,
        IReadOnlySet<Guid> alreadyAsked,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(alreadyAsked);
        var rows = await db.Securities.AsNoTracking()
            .Where(s => s.LedgerId == ledgerId
                        && s.QuoteCurrencyConfirmedAt == null
                        && s.QuoteSymbolPublic
                        && (s.QuoteSymbol != null || s.Ticker != null)
                        && db.SecurityPrices.Any(p => p.LedgerId == ledgerId
                                                      && p.SecurityId == s.Id
                                                      && p.Source == PriceSource.Fetch))
            .OrderBy(s => s.Id)
            .Select(s => new QuoteSecurityRef(s.Id, s.QuoteSymbol ?? s.Ticker!, s.QuoteSymbolPublic))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Where(r => !alreadyAsked.Contains(r.SecurityId)).ToList();
    }

    /// <summary>
    /// Look up each security's currency and confirm what the lookup named. A failed lookup
    /// confirms nothing for that security and is returned as its error.
    /// </summary>
    internal static async Task<(Outcome Outcome, IReadOnlyList<QuoteError> Errors)> LookupAndConfirmAsync(
        AppDbContext db,
        Guid ledgerId,
        IQuoteCurrencyLookup lookup,
        IReadOnlyList<QuoteSecurityRef> securities,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(clock);
        if (securities.Count == 0) return (Outcome.None, []);

        // The egress first, outside any transaction, so no row is held across a request.
        var found = await lookup.LookupCurrenciesAsync(securities, cancellationToken).ConfigureAwait(false);
        var outcome = await ConfirmAsync(
                db, ledgerId, found.Currencies, clock.GetUtcNow().UtcDateTime, cancellationToken)
            .ConfigureAwait(false);
        return (outcome, found.Errors);
    }
}
