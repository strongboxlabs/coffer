using Microsoft.AspNetCore.Http;

using Coffer.Api.Contracts;
using Coffer.Api.Errors;
using Coffer.Api.Ingest;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Shared ingest outcome → HTTP result mapping. The per-connection
/// sync endpoint, the per-account sync endpoint, and the sync-all
/// aggregator all dispatch the same <see cref="IngestPullOutcome"/>;
/// this helper keeps the error-code translation in one place so
/// they can't drift.
/// </summary>
/// <remarks>
/// Post ADR-0031 Phase 2b cutover the orchestrator returns
/// <see cref="IngestPullOutcome"/> in place of the pre-Phase 2
/// <c>SimpleFinSyncService.FailureOrResult</c>. The wire DTO
/// (<see cref="SyncResultDto"/>) is unchanged — behavior zero
/// from the SPA's perspective.
/// </remarks>
internal static class SyncEndpointMapping
{
    /// <summary>
    /// Map an ingest outcome to a 200 with <see cref="SyncResultDto"/>
    /// on success, or a typed 422 with the matching
    /// <see cref="BusinessError.Codes"/> on failure.
    /// </summary>
    public static IResult ToResult(IngestPullOutcome outcome)
    {
        if (!outcome.IsSuccess)
        {
            return outcome.Failure switch
            {
                IngestFailureReason.ConnectionNotFound =>
                    BusinessError.Problem(BusinessError.Codes.FeedConnectionNotFound,
                        "Feed connection not found in this ledger."),
                IngestFailureReason.AccessUrlMissing =>
                    BusinessError.Problem(BusinessError.Codes.FeedConnectionAccessUrlMissing,
                        "Feed connection has no stored access URL — recreate the connection."),
                IngestFailureReason.AccessUrlCorrupted =>
                    BusinessError.Problem(BusinessError.Codes.FeedConnectionAccessUrlCorrupted,
                        "Feed connection access URL could not be decrypted under the current master KEK. " +
                        "If the master KEK rotated, run the re-wrap job; otherwise recreate the connection."),
                IngestFailureReason.SyncInProgress =>
                    BusinessError.Problem(BusinessError.Codes.FeedSyncInProgress,
                        "Another sync is already running for this connection. Wait for it to finish."),
                _ => Results.Problem("Unknown sync failure.", statusCode: 500),
            };
        }

        return Results.Ok(ToDto(outcome.Result!));
    }

    /// <summary>
    /// Project the orchestrator-level <see cref="IngestRunOutcome"/>
    /// to the wire <see cref="SyncResultDto"/>. Pulled out of the
    /// per-connection endpoint so both sync-all and per-account
    /// callers share the same projection rules.
    /// </summary>
    public static SyncResultDto ToDto(IngestRunOutcome r) =>
        new(r.AccountsDiscovered,
            r.TransactionsForReview,
            r.TransactionsStillPending,
            r.AlreadyKnown,
            r.ConnectionStatus,
            r.Errors
                .Select(e => new SyncErrorDto(
                    e.Code, e.Message,
                    e.ConnectionId, e.AccountId))
                .ToList());

    /// <summary>
    /// Map a failure to the wire-level error code the
    /// <see cref="SyncAllConnectionEntry"/> exposes. Mirrors the
    /// switch in <see cref="ToResult"/> but returns just the code
    /// string for inline inclusion in the sync-all aggregate.
    /// </summary>
    public static string ToFailureCode(IngestFailureReason reason) =>
        reason switch
        {
            IngestFailureReason.ConnectionNotFound =>
                BusinessError.Codes.FeedConnectionNotFound,
            IngestFailureReason.AccessUrlMissing =>
                BusinessError.Codes.FeedConnectionAccessUrlMissing,
            IngestFailureReason.AccessUrlCorrupted =>
                BusinessError.Codes.FeedConnectionAccessUrlCorrupted,
            IngestFailureReason.SyncInProgress =>
                BusinessError.Codes.FeedSyncInProgress,
            _ => "unknown",
        };
}
