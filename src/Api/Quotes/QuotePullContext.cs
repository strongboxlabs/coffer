namespace Coffer.Api.Quotes;

/// <summary>
/// Inputs a <see cref="IQuotePullProvider"/> needs to fetch
/// prices for one ledger's tracked securities.
/// </summary>
/// <remarks>
/// <para><see cref="Securities"/> is the set the orchestrator
/// wants quotes for; the provider can return a subset (a
/// provider that doesn't know a ticker simply omits it).
/// SimpleFIN-holdings narrows internally to securities held on
/// SimpleFIN-mapped brokerages; Yahoo would try every ticker.</para>
///
/// <para>Per ADR-0033 §4, v1 providers don't need encrypted
/// per-ledger config so <see cref="LedgerWrappedLek"/> is on the
/// context anyway — it's the same pattern as ADR-0031's
/// <c>PullContext</c> and Yahoo (when it lands) may need to
/// unwrap a per-user API token stored under the ledger's LEK.</para>
/// </remarks>
public sealed record QuotePullContext(
    Guid LedgerId,
    /// <summary>The ledger's wrapped LEK (per ADR-0026) for
    /// providers that need to unwrap per-ledger secrets. Most
    /// v1 providers ignore it.</summary>
    byte[] LedgerWrappedLek,
    /// <summary>The securities the orchestrator wants quotes
    /// for. Each entry pairs the Coffer security id with its
    /// canonical ticker so a provider can resolve by either.</summary>
    IReadOnlyList<QuoteSecurityRef> Securities);

/// <summary>
/// One security in the quote-pull request set. <see cref="Ticker"/>
/// is the canonical symbol (the SimpleFIN-holdings matcher's
/// recovered symbol; the Yahoo-side ticker); <see cref="SecurityId"/>
/// is the Coffer id the orchestrator binds the resulting price to.
/// </summary>
public sealed record QuoteSecurityRef(
    Guid SecurityId,
    string Ticker,
    string CurrencyCode,
    /// <summary>Whether <see cref="Ticker"/> is a public market symbol
    /// (ADR-0054 D2). False = a private / feed-internal quote symbol: egress
    /// (opt-in) providers skip it; the no-egress feed provider still matches it.</summary>
    bool QuoteSymbolPublic = true);
