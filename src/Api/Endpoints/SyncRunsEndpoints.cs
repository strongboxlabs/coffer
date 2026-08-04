using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-connection sync activity log (slice 2c.1). Two reads —
/// list-by-connection (the FeedConnectionsPage activity panel) and
/// detail-by-id (the expandable per-run view with errors +
/// promotions). Writes happen on the sync path; no mutations here.
/// </summary>
public static class SyncRunsEndpoints
{
    /// <summary>Default page size for the list endpoint when the
    /// client omits <c>limit</c>. Sized to one screen of activity —
    /// the SPA shows ~5 inline + "view all" for the rest.</summary>
    public const int DefaultLimit = 50;

    public static IEndpointRouteBuilder MapSyncRunsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/sync-runs")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapGet("/", ListAsync);
        group.MapGet("/{runId:guid}", GetDetailAsync);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        Guid connectionId,
        int? limit,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerOperationsRepository syncRuns,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var rows = await syncRuns.ListByConnectionAsync(
            ledgerId, connectionId, limit ?? DefaultLimit, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetDetailAsync(
        Guid ledgerId,
        Guid runId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerOperationsRepository syncRuns,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var detail = await syncRuns.GetDetailAsync(
            ledgerId, runId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
            return BusinessError.Problem(BusinessError.Codes.SyncRunNotInLedger,
                "Sync run not found in this ledger.");
        return Results.Ok(detail);
    }
}
