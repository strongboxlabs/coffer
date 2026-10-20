using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Ledger overview aggregate (ADR-0056 slice 1) — the dashboard's financial
/// summary: net worth, per-account balances grouped by type, and an investment
/// roll-up, server-computed in one call.
/// </summary>
public static class OverviewEndpoints
{
    public static IEndpointRouteBuilder MapOverviewEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGroup("/api/ledgers/{ledgerId:guid}/overview")
              .RequireAuthorization()
              .RequireLedgerAccess()
              .MapGet("/", GetAsync);
        return routes;
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/overview</c> — the ledger summary.
    /// 422 on <c>ledger-not-visible</c>.
    /// </summary>
    private static async Task<IResult> GetAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        OverviewRepository overview,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var dto = await overview.GetAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(dto);
    }
}
