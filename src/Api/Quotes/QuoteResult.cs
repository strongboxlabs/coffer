namespace Coffer.Api.Quotes;

/// <summary>
/// Output of <see cref="IQuotePullProvider.PullAsync"/> /
/// <see cref="IQuotePushProvider.PushAsync"/>. Carries the
/// per-security price entries the provider produced, plus a
/// typed error list for partial-failure surfacing.
/// </summary>
/// <remarks>
/// Errors are per-security (or per-batch); a provider that fails
/// entirely throws an exception that the orchestrator maps to a
/// failed run. The partial-failure path is when a provider got
/// most quotes but a few tickers were unresolved / rate-limited /
/// invalid.
/// </remarks>
public sealed record QuoteResult(
    IReadOnlyList<QuoteEntry> Quotes,
    IReadOnlyList<QuoteError> Errors);

/// <summary>
/// One per-security price entry. The orchestrator UPSERTs this
/// into <c>security_prices</c> on conflict
/// <c>(security_id, price_date)</c>.
/// </summary>
public sealed record QuoteEntry(
    Guid SecurityId,
    decimal Price,
    /// <summary>UTC instant when the source reported this price
    /// as accurate. SimpleFIN: the account's
    /// <c>balance_date_unix</c>. Yahoo: the quote-snapshot
    /// timestamp.</summary>
    DateTime PriceAsOfUtc,
    string CurrencyCode,
    /// <summary>Origin tag for the source-priority upsert (ADR-0070) —
    /// <c>PriceSource.Fetch</c> (Yahoo) or <c>PriceSource.Simplefin</c>.</summary>
    string Source);

/// <summary>
/// Provider-side partial failure on one security. <see cref="Code"/>
/// is a short stable identifier (e.g. <c>"ticker-not-resolved"</c>,
/// <c>"rate-limited"</c>, <c>"market-value-zero-shares"</c>); the
/// SPA may display the code as a class on the per-position chip
/// once a refresh-status surface ships.
/// </summary>
public sealed record QuoteError(
    Guid? SecurityId,
    string Ticker,
    string Code,
    string Message);
