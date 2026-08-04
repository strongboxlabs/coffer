namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Persistable shape of a row in <c>security_prices</c>. One snapshot per
/// security per <see cref="PriceDate"/>; the unique index
/// <c>(security_id, price_date)</c> keys idempotency. <see cref="High"/>,
/// <see cref="Low"/>, and <see cref="Volume"/> are nullable because
/// manually-entered MD price points often carry only the close.
/// </summary>
public sealed record SecurityPriceRow(
    Guid Id,
    Guid LedgerId,
    Guid SecurityId,
    decimal Price,
    string CurrencyCode,
    DateTimeOffset PriceDate,
    decimal? High,
    decimal? Low,
    long? Volume);
