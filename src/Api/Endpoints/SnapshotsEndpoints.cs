using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Snapshots;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger snapshot endpoints (ADR-0037). Server-side capped
/// snapshots of the user-curated ledger graph. Four routes:
///
///   * <c>POST /api/ledgers/{lid}/snapshots</c> — create a manual snap.
///   * <c>GET /api/ledgers/{lid}/snapshots</c> — list up to 5, newest first.
///   * <c>POST /api/ledgers/{lid}/snapshots/{sid}/restore</c> — in-place restore.
///   * <c>DELETE /api/ledgers/{lid}/snapshots/{sid}</c> — remove one.
///
/// Auto-snaps fire via <see cref="SnapshotScheduler"/>, not through
/// these endpoints.
/// </summary>
public static class SnapshotsEndpoints
{
    /// <summary>Max chars for the manual snap description field.</summary>
    public const int DescriptionMaxLength = 200;

    public static IEndpointRouteBuilder MapSnapshotsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/snapshots")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapPost("/{snapshotId:guid}/restore", RestoreAsync).AsLedgerOwner();
        group.MapDelete("/{snapshotId:guid}", DeleteAsync);

        // Auto-snapshot scheduling is the generic SchedulesEndpoints
        // (/schedules/snapshot) — not here.
        return routes;
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/snapshots</c>. Create a manual
    /// snapshot. Walks the ledger's in-scope graph + persists. 422
    /// <c>snapshot-manual-at-cap</c> when 5 snapshots already exist.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        Guid ledgerId,
        CreateSnapshotRequest? request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerSnapshotsRepository snapshots,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var description = NormalizeDescription(request?.Description);

        var result = await snapshots.CreateAsync(
            ledgerId,
            kind: "manual",
            createdByUserId: currentUser.UserId,
            description: description,
            cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            LedgerSnapshotsRepository.CreateOutcome.Created =>
                Results.Ok(new CreateSnapshotResponse(ToSummary(result.Row!))),
            LedgerSnapshotsRepository.CreateOutcome.AtCap =>
                BusinessError.Problem(BusinessError.Codes.SnapshotManualAtCap,
                    "This ledger has 5 snapshots already. Delete one before creating another."),
            // SkippedDueToFullPool can only fire on kind='auto', which this
            // endpoint never invokes. Defensive fall-through.
            _ => Results.Problem("Unexpected snapshot create outcome.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/snapshots</c>. Returns up to 5
    /// snapshots, newest-first. No content blob — that's
    /// internal-only.
    /// </summary>
    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerSnapshotsRepository snapshots,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var rows = await snapshots.ListAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(rows.Select(ToSummary).ToList());
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/snapshots/{snapshotId}/restore</c>.
    /// Restore the snapshot in place. Refuses on schema-version
    /// mismatch (Phase 1, per ADR-0037).
    /// </summary>
    private static async Task<IResult> RestoreAsync(
        Guid ledgerId,
        Guid snapshotId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerSnapshotsRepository snapshots,
        LedgerOperationsRepository operations,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await snapshots.RestoreAsync(
            ledgerId, snapshotId, cancellationToken).ConfigureAwait(false);

        if (outcome == LedgerSnapshotsRepository.RestoreOutcome.Restored)
        {
            // Durable audit (ADR-0055/0086): a restore replaces the ledger's data in
            // place, so it earns a ledger_operations row + an app-log line (import
            // already logs; restore was previously silent). The owner (this is an
            // owner-only route) may INSERT for their own ledger under the existing
            // per-user RLS policy.
            await operations.RecordTerminalAsync(
                ledgerId: ledgerId,
                family: LedgerOperationsRepository.SnapshotRestoreFamily,
                providerKey: LedgerOperationsRepository.SnapshotRestoreProviderKey,
                triggeredVia: "manual",
                triggeredByUserId: currentUser.UserId,
                status: "completed",
                errorMessage: null,
                detailsJson: JsonSerializer.Serialize(new { snapshot_id = snapshotId }),
                completedAt: DateTime.UtcNow,
                cancellationToken).ConfigureAwait(false);

            loggerFactory.CreateLogger("Coffer.Api.Snapshots.Restore").LogInformation(
                "Snapshot {SnapshotId} restored into ledger {LedgerId} by user {UserId}.",
                snapshotId, ledgerId, currentUser.UserId);

            return Results.NoContent();
        }

        return outcome switch
        {
            LedgerSnapshotsRepository.RestoreOutcome.Restored => Results.NoContent(),
            LedgerSnapshotsRepository.RestoreOutcome.NotFound =>
                BusinessError.Problem(BusinessError.Codes.SnapshotNotFound,
                    "Snapshot not found."),
            LedgerSnapshotsRepository.RestoreOutcome.WrongLedger =>
                BusinessError.Problem(BusinessError.Codes.SnapshotNotFound,
                    "Snapshot does not belong to this ledger."),
            LedgerSnapshotsRepository.RestoreOutcome.SchemaVersionMismatch =>
                BusinessError.Problem(BusinessError.Codes.SnapshotSchemaVersionMismatch,
                    "Snapshot was taken on a different schema version; restore not supported in this release."),
            LedgerSnapshotsRepository.RestoreOutcome.PayloadCorrupt =>
                BusinessError.Problem(BusinessError.Codes.SnapshotPayloadCorrupt,
                    "Snapshot payload could not be decoded; the snapshot may be from a different format version."),
            _ => Results.Problem("Unexpected snapshot restore outcome.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>DELETE /api/ledgers/{ledgerId}/snapshots/{snapshotId}</c>.
    /// Remove one snapshot. Idempotent — non-existent ids return
    /// 204 anyway (no info leak about which snapshots existed).
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid ledgerId,
        Guid snapshotId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerSnapshotsRepository snapshots,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        await snapshots.DeleteAsync(ledgerId, snapshotId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static string? NormalizeDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length > DescriptionMaxLength)
            trimmed = trimmed.Substring(0, DescriptionMaxLength);
        return trimmed;
    }

    private static SnapshotSummaryDto ToSummary(Coffer.Api.Db.Entities.LedgerSnapshotRow row)
        => new(
            Id: row.Id,
            CreatedAt: row.CreatedAt,
            CreatedByUserId: row.CreatedByUserId,
            Kind: row.Kind,
            Description: row.Description,
            SchemaVersion: row.SchemaVersion,
            ContentSizeUncompressed: row.ContentSizeUncompressed);
}
