namespace Coffer.Api.Ingest;

/// <summary>
/// One partial-failure entry returned from
/// <see cref="IPullProvider"/> / <see cref="IFileProvider"/>.
/// Surfaced to the SPA via <c>sync_run_errors</c> so the user can
/// see which institution / account / row failed without aborting
/// the whole sync.
/// </summary>
/// <remarks>
/// Structured to mirror SimpleFIN's <c>errlist[]</c> shape:
/// machine-parsable <see cref="Code"/> for SPA dispatch + human
/// <see cref="Message"/> for display. Optional
/// <see cref="ConnectionId"/> / <see cref="AccountId"/> let the
/// orchestrator scope the error to the right row when only part
/// of a multi-account pull failed.
/// </remarks>
public sealed record IngestError(
    string Code,
    string Message,
    string? ConnectionId,
    string? AccountId);
