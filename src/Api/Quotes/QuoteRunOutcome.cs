namespace Coffer.Api.Quotes;

/// <summary>
/// Result envelope from a <c>QuoteOrchestrator.RunPullAsync</c> /
/// <c>RunPushAsync</c> / <c>RunAllPullsAsync</c> invocation.
/// Carries the per-security counts the SPA renders in the
/// "Refresh complete" toast plus the provider's typed error list
/// for partial-failure surfacing.
/// </summary>
/// <remarks>
/// No <c>sync_runs</c>-style audit table backs this in v1 per
/// ADR-0033 §3 — outcome is purely the response envelope from
/// the user-initiated refresh. If a future failure mode warrants
/// a persistent audit, add a <c>quote_runs</c> table then.
/// </remarks>
public sealed record QuoteRunOutcome(
    /// <summary>Provider keys this orchestrator invocation
    /// touched. <c>RunPullAsync</c> / <c>RunPushAsync</c> contain
    /// exactly one; <c>RunAllPullsAsync</c> contains every
    /// pull-capable provider that ran.</summary>
    IReadOnlyList<string> ProviderKeys,
    /// <summary>Number of <c>security_prices</c> rows newly
    /// inserted (i.e. no prior price on that
    /// <c>(security, date)</c>).</summary>
    int PricesInserted,
    /// <summary>Number of existing <c>security_prices</c> rows
    /// updated via UPSERT (same <c>(security, date)</c>, but
    /// price / volume / etc. refreshed).</summary>
    int PricesUpdated,
    /// <summary>Count of prices WRITTEN (inserted or updated) this run, keyed by
    /// ADR-0070 source (<c>fetch</c> = market data / Yahoo, <c>simplefin</c> =
    /// feed-derived, …). Lets the activity log attribute which provider actually
    /// moved each price, rather than showing a generic "quote refresh".</summary>
    IReadOnlyDictionary<string, int> PricesWrittenBySource,
    /// <summary>Securities that were requested but no provider
    /// returned a quote for. Renders in the SPA as a yellow
    /// "couldn't refresh" pill so the user knows which positions
    /// stayed stale.</summary>
    IReadOnlyList<Guid> SecuritiesUnresolved,
    IReadOnlyList<QuoteError> Errors);
