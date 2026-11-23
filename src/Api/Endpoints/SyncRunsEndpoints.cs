using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// One run's detail: its summary, errors and promote-on-clear events. Backs the
/// expandable row in Settings → Activity, which lists runs of every family from
/// <c>/ledger-operations</c> and opens one here. The id is a <c>ledger_operations</c>
/// id, so a quote refresh's errors read the same way a sync's do. Writes happen on
/// the run's own path; no mutations here.
/// </summary>
/// <remarks>
/// There was a per-connection list here (<c>GET /sync-runs?connectionId=</c>) for the
/// Bank feeds panel's own activity strip. The strip was removed, Activity being the
/// one place runs are read, and the list went with it: a path the SPA does not call
/// is not kept.
/// </remarks>
public static class SyncRunsEndpoints
{
    public static IEndpointRouteBuilder MapSyncRunsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGroup("/api/ledgers/{ledgerId:guid}/sync-runs")
              .RequireAuthorization()
              .RequireLedgerAccess()
              .MapGet("/{runId:guid}", GetDetailAsync);

        return routes;
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
