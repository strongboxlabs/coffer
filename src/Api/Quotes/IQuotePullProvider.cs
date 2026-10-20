namespace Coffer.Api.Quotes;

/// <summary>
/// Pull-capable quote provider (ADR-0033). The orchestrator
/// invokes <see cref="PullAsync"/> against the provider; the
/// provider produces a typed <see cref="QuoteResult"/> the
/// orchestrator persists. No DB writes inside the provider —
/// same boundary as ADR-0031's ingest providers.
/// </summary>
/// <remarks>
/// <para>A provider that ALSO supports push implements
/// <c>IQuotePushProvider</c> in the same class. The orchestrator
/// resolves both via DI (one registration per interface).</para>
///
/// <para>Concrete pull providers today:
/// <list type="bullet">
/// <item><c>SimpleFinHoldingsQuoteProvider</c> — reads stored
///   per-account raw payload from the SimpleFIN ingest
///   orchestrator's writes. No external HTTP.</item>
/// <item><c>YahooFinanceQuoteProvider</c> (ADR-0054) — HTTP EOD
///   fetch against query1.finance.yahoo.com; opt-in via
///   <c>Quotes:Yahoo:Enabled</c>.</item>
/// </list></para>
/// </remarks>
public interface IQuotePullProvider
{
    /// <summary>Stable identifier; matches the registry key used
    /// by <c>QuoteOrchestrator</c> dispatch. Lowercase, hyphenated
    /// (e.g. <c>"simplefin-holdings"</c>, <c>"yahoo"</c>).</summary>
    string ProviderKey { get; }

    /// <summary>Human label for the settings UI (e.g. "Yahoo Finance").</summary>
    string DisplayName { get; }

    /// <summary>
    /// True when the provider makes external egress and must be explicitly
    /// enabled per ledger (ADR-0057 <c>quotes</c> pref). The orchestrator runs
    /// an opt-in provider only when the acting ledger's pref lists its key;
    /// non-opt-in providers (e.g. <c>simplefin-holdings</c>, which reads stored
    /// payloads) always run.
    /// </summary>
    bool RequiresOptIn { get; }

    /// <summary>Fetch the latest available prices for the
    /// supplied securities. Best-effort: a provider that can't
    /// resolve a ticker omits it from <see cref="QuoteResult.Quotes"/>
    /// and (optionally) adds a typed entry to
    /// <see cref="QuoteResult.Errors"/>.</summary>
    Task<QuoteResult> PullAsync(
        QuotePullContext context,
        CancellationToken cancellationToken);
}
