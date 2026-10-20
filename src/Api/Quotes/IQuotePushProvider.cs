namespace Coffer.Api.Quotes;

/// <summary>
/// Push-capable quote provider (ADR-0033). The orchestrator
/// accepts a payload from the caller (file upload, SPA bulk
/// entry, future webhook) and invokes <see cref="PushAsync"/>
/// on the matching provider to parse it into a
/// <see cref="QuoteResult"/>. No DB writes inside the
/// provider — orchestrator owns persistence.
/// </summary>
/// <remarks>
/// <para>A provider that ALSO supports pull implements
/// <c>IQuotePullProvider</c> in the same class.</para>
///
/// <para>No concrete push providers in v1. The interface is
/// declared here so the orchestrator surface is parallel to
/// ingest's (ADR-0031) and so future push-only providers (CSV
/// upload, webhook, manual bulk) plug in without an
/// orchestrator refactor.</para>
/// </remarks>
public interface IQuotePushProvider
{
    /// <summary>Stable identifier; matches the registry key
    /// used by <c>QuoteOrchestrator</c> dispatch. Lowercase,
    /// hyphenated.</summary>
    string ProviderKey { get; }

    /// <summary>Parse the caller-supplied payload into a
    /// <see cref="QuoteResult"/>. The payload's shape is
    /// per-provider (CSV bytes, webhook JSON, SPA bulk entry
    /// rows). The orchestrator routes by
    /// <see cref="QuotePushPayload.ProviderKey"/> and the
    /// matching provider casts / parses internally.</summary>
    Task<QuoteResult> PushAsync(
        QuotePushPayload payload,
        CancellationToken cancellationToken);
}
