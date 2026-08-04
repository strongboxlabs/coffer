namespace Coffer.Api.Ingest;

/// <summary>
/// Pull-based ingest provider (ADR-0031 §1). A pull provider owns
/// a long-lived connection (auth credentials + institution
/// metadata) and is polled on a schedule or on user demand. The
/// provider is a <em>pure translator</em>: it fetches the foreign
/// payload, parses it into typed records, and returns them. It
/// does NOT write to the DB, does NOT manage <c>sync_runs</c>,
/// does NOT compute dedup keys — that logic lives on
/// <see cref="IngestOrchestrator"/>.
/// </summary>
public interface IPullProvider
{
    /// <summary>
    /// Stable identifier persisted on
    /// <c>feed_connections.provider</c>. Used by the orchestrator
    /// to dispatch incoming sync requests to the right provider.
    /// Example values: <c>"simplefin"</c>.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Pull the latest payload using the given context and return
    /// typed records. Implementations should map transient HTTP /
    /// parsing errors to <see cref="PullResult.Errors"/> (partial
    /// failure) and reserve thrown exceptions for unrecoverable
    /// faults the orchestrator should surface as a failed
    /// <c>sync_runs</c> row.
    /// </summary>
    Task<PullResult> PullAsync(
        PullContext context,
        CancellationToken cancellationToken);
}
