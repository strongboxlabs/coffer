namespace Coffer.Api.Ingest;

/// <summary>
/// Result of an <see cref="IngestOrchestrator"/> run — pull or
/// file. The orchestrator returns this to the calling endpoint so
/// callers can surface success / partial-failure / failure to the
/// SPA without re-querying <c>sync_runs</c>.
/// </summary>
/// <remarks>
/// Field set matches what the pre-ADR-0031 <c>SimpleFinSyncService.SyncResult</c>
/// surfaced so the SPA wire shape (<c>SyncResultDto</c>) stays
/// unchanged through the Phase 2 retrofit. File providers fill the
/// pull-specific fields with neutral defaults
/// (<see cref="ConnectionStatus"/> = <c>"active"</c>,
/// <see cref="AccountsDiscovered"/> = 1).
/// </remarks>
public sealed record IngestRunOutcome(
    /// <summary>The <c>sync_runs.id</c> row created for this
    /// orchestrator run. Lets the SPA navigate to the run's detail
    /// panel.</summary>
    Guid SyncRunId,
    /// <summary>Number of provider-side accounts the provider
    /// returned (mapped + unmapped). Pull providers: every account
    /// on the connection. File providers: 1.</summary>
    int AccountsDiscovered,
    /// <summary>Bank-posted rows inserted as <c>needs_review=true</c>
    /// + promotions of pre-existing pending rows that just
    /// cleared.</summary>
    int TransactionsForReview,
    /// <summary>Bank-pending rows inserted with
    /// <c>is_pending=true</c>. Not yet promotable — the bank itself
    /// hasn't cleared them. Always 0 for file providers.</summary>
    int TransactionsStillPending,
    /// <summary>Rows the orchestrator dedup-skipped because they
    /// FITID-matched an existing <c>txn_headers</c> row.</summary>
    int AlreadyKnown,
    /// <summary>Post-run <c>feed_connections.status</c>. Normally
    /// <c>"active"</c>; <c>"needs_reauth"</c> when the provider
    /// reported a revoked / expired token (SimpleFIN 403).</summary>
    string ConnectionStatus,
    /// <summary>Provider-side partial failures, persisted to
    /// <c>sync_run_errors</c>.</summary>
    IReadOnlyList<IngestError> Errors);
