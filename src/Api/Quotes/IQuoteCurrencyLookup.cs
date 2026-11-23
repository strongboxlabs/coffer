namespace Coffer.Api.Quotes;

/// <summary>
/// A market-data provider that can say which currency a symbol is quoted in without
/// storing a price (ADR-0114 D5, amendment): the currency-only lookup the quote refresh's
/// one-time pass and the <c>quote_currency</c> consistency repair run.
/// </summary>
/// <remarks>
/// Egress. A caller runs it only under the acting user's opt-in for the provider's key
/// (<see cref="ProviderKey"/>), and only for public symbols (ADR-0054 D2).
/// </remarks>
public interface IQuoteCurrencyLookup
{
    /// <summary>The opt-in key that gates the lookup (the provider's own).</summary>
    string ProviderKey { get; }

    /// <summary>The currency each symbol is quoted in, in its major unit; a symbol that
    /// does not resolve is an error with the provider's per-symbol code, and no currency.</summary>
    Task<QuoteCurrencyLookupResult> LookupCurrenciesAsync(
        IReadOnlyList<QuoteSecurityRef> securities, CancellationToken cancellationToken);
}

/// <summary>One security's currency, as a lookup named it: the major unit, and how many of
/// the quoted units make one (100 for <c>GBp</c>, 1 for a symbol quoted in its major unit).</summary>
public sealed record QuoteCurrency(Guid SecurityId, string CurrencyCode, int QuotedMinorUnitFactor = 1);

/// <summary>What a currency lookup found: the currencies it read, and the securities it
/// could not.</summary>
public sealed record QuoteCurrencyLookupResult(
    IReadOnlyList<QuoteCurrency> Currencies,
    IReadOnlyList<QuoteError> Errors);
