namespace Coffer.Api.Ingest;

/// <summary>
/// Pre-run failure modes the orchestrator can detect before it
/// even calls a provider. Mirrors the pre-ADR-0031
/// <c>SimpleFinSyncService.FailureReason</c> values so the
/// endpoint mapping → HTTP / 422 code translation stays
/// unchanged through Phase 2 retrofit.
/// </summary>
public enum IngestFailureReason
{
    /// <summary>The supplied connection id doesn't exist in the
    /// caller's ledger.</summary>
    ConnectionNotFound,
    /// <summary>Connection row exists but has no stored access
    /// URL ciphertext (recreate the connection).</summary>
    AccessUrlMissing,
    /// <summary>Access URL ciphertext couldn't be decrypted under
    /// the current master KEK (rotate / recreate).</summary>
    AccessUrlCorrupted,
    /// <summary>Another sync is already in flight for this
    /// connection — API-layer fast-path lock OR DB-level UNIQUE
    /// partial index <c>uq_sync_runs_one_running_per_connection</c>
    /// fired.</summary>
    SyncInProgress,
}

/// <summary>
/// Discriminated outcome of an orchestrator pull run. Either a
/// pre-run failure surfaced as a typed 422 by the endpoint, or
/// a successful <see cref="IngestRunOutcome"/>.
/// </summary>
public sealed record IngestPullOutcome(
    IngestFailureReason? Failure,
    IngestRunOutcome? Result)
{
    public bool IsSuccess => Failure is null && Result is not null;

    public static IngestPullOutcome Ok(IngestRunOutcome r) => new(null, r);
    public static IngestPullOutcome Fail(IngestFailureReason f) => new(f, null);
}
