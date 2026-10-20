using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db;
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
        ServiceDbContextFactory serviceFactory,
        ILoggerFactory loggerFactory,
        MetaRepository meta,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Captured through the SERVICE role, like the scheduled snapshot, once the
        // caller's access is established above. Under the caller's RLS another
        // member's private saved reports, pins and account groups were invisible:
        // never captured, never restored — so a manual snapshot and an automatic
        // one of the same ledger held different data (mig 247).
        await using var svc = serviceFactory.Create();
        var snapshots = new LedgerSnapshotsRepository(svc, loggerFactory.CreateLogger<LedgerSnapshotsRepository>(), clock);

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
                Results.Ok(new CreateSnapshotResponse(ToSummary(
                    result.Row!,
                    await meta.GetLatestSchemaScriptAsync(cancellationToken)
                        .ConfigureAwait(false) ?? string.Empty))),
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
        MetaRepository meta,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var rows = await snapshots.ListAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        // Read once for the whole list, not per row.
        var liveSchemaVersion = await meta.GetLatestSchemaScriptAsync(cancellationToken)
            .ConfigureAwait(false) ?? string.Empty;
        return Results.Ok(rows.Select(r => ToSummary(r, liveSchemaVersion)).ToList());
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
        ServiceDbContextFactory serviceFactory,
        ILoggerFactory loggerFactory,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        // Through the SERVICE role, for the reason CreateAsync gives: a restore must
        // replace every member's rows, and under the owner's RLS it could neither
        // delete nor re-insert another member's private ones. The owner check is
        // the route's (.AsLedgerOwner) and the visibility check above.
        await using var svc = serviceFactory.Create();
        var snapshots = new LedgerSnapshotsRepository(svc, loggerFactory.CreateLogger<LedgerSnapshotsRepository>(), clock);
        var operations = new LedgerOperationsRepository(svc);

        // Durable audit (ADR-0055/0086): a restore replaces the ledger's data in
        // place, so it earns a ledger_operations row + an app-log line (import
        // already logs; restore was previously silent). Written on the service
        // context the restore runs on (this route is owner-only; the role is the
        // API's check, not RLS's). Written INSIDE the restore transaction, so the
        // restore and its record commit together — and a failure here rolls the
        // restore back, which keeps the failure dialog's "your ledger data was not
        // changed" true.
        var outcome = await snapshots.RestoreAsync(
            ledgerId, snapshotId,
            beforeCommit: ct => operations.RecordTerminalAsync(
                ledgerId: ledgerId,
                family: LedgerOperationsRepository.SnapshotRestoreFamily,
                providerKey: LedgerOperationsRepository.SnapshotRestoreProviderKey,
                triggeredVia: "manual",
                triggeredByUserId: currentUser.UserId,
                status: "completed",
                errorMessage: null,
                detailsJson: JsonSerializer.Serialize(new { snapshot_id = snapshotId }),
                completedAt: clock.GetUtcNow().UtcDateTime,
                ct),
            cancellationToken).ConfigureAwait(false);

        if (outcome == LedgerSnapshotsRepository.RestoreOutcome.Restored)
        {
            // A restore replaces the rows the last consistency verdict was about,
            // so that verdict is no longer a statement about anything. Null it —
            // "unknown" rather than a guess in either direction (mig 242).
            //
            // This is not theoretical tidiness. Before the cadence gate the
            // monitor re-checked every 15 minutes, so a restored ledger was
            // re-verified almost immediately; with a 12-hour cadence a restore
            // that brought back a snapshot captured WHILE drifted would sit
            // behind a stale green for half a day. Nulling LastCheckedAt makes
            // the ledger due on the next tick, without this endpoint knowing what
            // the cadence is.
            //
            // Through the SERVICE context because the table has no write policy
            // for the app role, and AFTER the restore transaction rather than
            // inside it: the restore functions run as the caller, so the write
            // could not join that transaction anyway, and the failure direction
            // is safe — a verdict that stays stale is where we already were. So a
            // failure is logged, not returned: the restore HAS committed, and a 500
            // would tell the user it had not. Not tied to the request's token for
            // the same reason — a client that hangs up does not undo the restore.
            //
            // A row is never CREATED here. No row already means "never checked",
            // which is what an invalidation is trying to say.
            var log = loggerFactory.CreateLogger("Coffer.Api.Snapshots.Restore");
            try
            {
                var now = clock.GetUtcNow().UtcDateTime;
                await using var verdicts = serviceFactory.Create();
                await verdicts.LedgerConsistencyStates
                    .Where(r => r.LedgerId == ledgerId)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(r => r.LastCheckedAt, (DateTime?)null)
                        .SetProperty(r => r.Healthy, (bool?)null)
                        .SetProperty(r => r.FailingProjections, Array.Empty<string>())
                        .SetProperty(r => r.UpdatedAt, now),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex,
                    "Snapshot {SnapshotId} restored into ledger {LedgerId}, but its consistency verdict " +
                    "could not be invalidated; it stays stale until the next scheduled check.",
                    snapshotId, ledgerId);
            }

            log.LogInformation(
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

    /// <param name="liveSchemaVersion">
    /// The latest applied migration script name. Compared ORDINALLY, the same
    /// way <c>LedgerSnapshotsRepository.RestoreAsync</c> compares it before
    /// refusing — if these two ever disagree the panel offers a Restore the
    /// server will reject, which is the defect this field exists to close.
    /// </param>
    private static SnapshotSummaryDto ToSummary(
        Coffer.Api.Db.Entities.LedgerSnapshotRow row,
        string liveSchemaVersion)
        => new(
            Id: row.Id,
            CreatedAt: row.CreatedAt,
            CreatedByUserId: row.CreatedByUserId,
            Kind: row.Kind,
            Description: row.Description,
            SchemaVersion: row.SchemaVersion,
            ContentSizeUncompressed: row.ContentSizeUncompressed,
            Restorable: string.Equals(
                row.SchemaVersion, liveSchemaVersion, StringComparison.Ordinal));
}
