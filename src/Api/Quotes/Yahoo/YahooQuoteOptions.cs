namespace Coffer.Api.Quotes.Yahoo;

/// <summary>
/// Throttle tuning for the Yahoo market-data provider (ADR-0054), bound from
/// the <c>Quotes:Yahoo</c> section. The knobs keep the sequential pull polite to
/// Yahoo's unofficial, rate-limited endpoint. Whether Yahoo runs at all is a
/// per-ledger user preference (ADR-0057 <c>quotes</c>), not config.
/// </summary>
public sealed class YahooQuoteOptions
{
    public const string SectionName = "Quotes:Yahoo";

    /// <summary>Delay between sequential per-symbol requests, in milliseconds.
    /// Throttles the burst rate so a large held book doesn't trip Yahoo's
    /// rate limiter. 0 disables the delay.</summary>
    public int RequestDelayMs { get; set; } = 250;

    /// <summary>Upper bound (seconds) on the post-429 backoff. The provider
    /// honors the response's <c>Retry-After</c> up to this cap before moving to
    /// the next symbol.</summary>
    public int MaxBackoffSeconds { get; set; } = 5;
}
