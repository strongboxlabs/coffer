using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json.Typed;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Mappers;

/// <summary>
/// Pure translation from a Moneydance <see cref="MdCsnap"/> into a Coffer
/// <see cref="SecurityPriceRow"/>. csnap items reference a <c>curr</c> via
/// <see cref="MdCsnap.CurrId"/>; only those that point at a security-typed
/// currency become rows here. Plain-currency snapshots (USD/EUR/etc.
/// exchange-rate samples) are out of scope.
/// </summary>
public static class PriceSnapshotMapper
{
    public enum SkipReason
    {
        UnknownSecurity,        // CurrId doesn't resolve to a known security
        MissingPrice,           // csnap has no usable rate value
        UnparseableDate,        // dt + price_date both unusable
    }

    public sealed record MapResult(SecurityPriceRow? Row, SkipReason? Skip);

    /// <param name="currencyCode">The currency the snapshot's rate is relative to, which
    /// the inverted price is in: the file's base currency. Moneydance quotes a snapshot
    /// against its security's relative currency when the security has one, and against
    /// the base currency otherwise; the export field that would name a relative
    /// currency is not known, so the base currency is the one this importer can
    /// prove (see <c>PriceSnapshotImportStep</c>).</param>
    public static MapResult Map(
        MdCsnap csnap,
        IReadOnlyDictionary<string, SecurityRef> securityByMdId,
        string currencyCode,
        Guid ledgerId = default)
    {
        ArgumentNullException.ThrowIfNull(csnap);
        ArgumentNullException.ThrowIfNull(securityByMdId);
        if (!MdCurrencies.IsCurrencyCode(currencyCode))
            throw new ArgumentException($"'{currencyCode}' is not a currency code in stored form.", nameof(currencyCode));

        if (!securityByMdId.TryGetValue(csnap.CurrId, out var securityRef))
            return new MapResult(null, SkipReason.UnknownSecurity);

        if (csnap.Rate is null || csnap.Rate.Value == 0m)
            return new MapResult(null, SkipReason.MissingPrice);

        var date = ResolvePriceDate(csnap);
        if (date is null) return new MapResult(null, SkipReason.UnparseableDate);

        // A security's `rate` is 1/share_price: shares per one unit of the currency
        // the rate is relative to. Invert to get the price, in that currency.
        // High and low swap meaning under the inversion: a higher `rate` means
        // a lower price, so urt.hi → price.low and urt.lo → price.high.
        var price = 1m / csnap.Rate.Value;

        // Sanity gate: an MD snapshot can be corrupt, with a rate whose
        // inverted price clears any plausible tradeable-security range. Skip rather than persist garbage prices
        // that would distort portfolio totals.
        if (price > 1_000_000m)
            return new MapResult(null, SkipReason.MissingPrice);
        var priceHigh = csnap.Low  is { } low  && low  > 0m ? 1m / low  : (decimal?)null;
        var priceLow  = csnap.High is { } high && high > 0m ? 1m / high : (decimal?)null;

        // Round to schema scale (NUMERIC(19,4)) before the consistency
        // check, so the stored values are exactly what we evaluated against.
        decimal? roundedHigh = priceHigh is { } ph ? decimal.Round(ph, 4) : null;
        decimal? roundedLow  = priceLow  is { } pl ? decimal.Round(pl, 4) : null;

        // A csnap's hi/lo can be inverted or otherwise inconsistent — nothing
        // in the export format guarantees hi >= lo. The schema CHECK
        // requires high >= low; rather than reject the snapshot, drop both
        // and keep only the close. The close is the load-bearing value for
        // every downstream report.
        if (roundedHigh is { } rh && roundedLow is { } rl && rh < rl)
        {
            roundedHigh = null;
            roundedLow  = null;
        }

        return new MapResult(new SecurityPriceRow(
            Id:           Guid.NewGuid(),
            LedgerId:     ledgerId,
            SecurityId:   securityRef.Id,
            Price:        decimal.Round(price, 4),
            CurrencyCode: currencyCode,
            PriceDate:    date.Value,
            High:         roundedHigh,
            Low:          roundedLow,
            Volume:       csnap.Volume), Skip: null);
    }

    /// <summary>
    /// Prefer the precise <c>price_date</c> millis (which carries time-of-day)
    /// when present; fall back to the integer <c>dt</c> field (yyyymmdd, midnight UTC).
    /// </summary>
    private static DateTimeOffset? ResolvePriceDate(MdCsnap csnap)
    {
        if (csnap.PriceDateMillis is { } millis && millis > 0)
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(millis); }
            catch (ArgumentOutOfRangeException) { /* fall through */ }
        }
        return TransactionMapper.ParseMdDate(csnap.Date == 0 ? null : csnap.Date);
    }
}
