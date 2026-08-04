namespace Coffer.Api.Quotes;

/// <summary>
/// Payload handed to <see cref="IQuotePushProvider.PushAsync"/>
/// when the caller (file upload endpoint, SPA bulk-entry, future
/// webhook handler) is providing the price data.
/// </summary>
/// <remarks>
/// Shape is per-provider — file-upload providers receive a byte
/// stream + filename; webhook providers receive parsed JSON; SPA
/// bulk-entry receives typed rows. The orchestrator routes by
/// <see cref="ProviderKey"/> and the matching provider casts
/// <see cref="Body"/> to whatever shape it expects.
///
/// No push providers in v1; the type is declared so the
/// orchestrator surface stays parallel to ingest's
/// (ADR-0031 §1) and so future push-only providers slot in
/// without an orchestrator refactor.
/// </remarks>
public sealed record QuotePushPayload(
    Guid LedgerId,
    string ProviderKey,
    /// <summary>Provider-specific payload. The receiver casts
    /// to its expected shape; mismatch is an
    /// InvalidCastException at provider entry (caller bug).</summary>
    object Body);
