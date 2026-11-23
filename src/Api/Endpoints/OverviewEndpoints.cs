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
    /// <c>GET /api/ledgers/{ledgerId}/overview</c> — the ledger summary, and with
    /// <c>?since=</c> net worth at the end of that month beside it.
    /// 422 on <c>ledger-not-visible</c>, and on <c>overview-since-invalid</c> when
    /// <c>since</c> is not a month-end before <c>today</c>, or comes without it.
    /// </summary>
    private static async Task<IResult> GetAsync(
        Guid ledgerId,
        // The caller's local date (?today=), as the register takes it: every figure
        // counts exactly the rows the register does not call Scheduled.
        DateOnly? today,
        // A month-end strictly before today. A month-end because the comparison is
        // "since last month" and nothing else; before today because a comparison
        // with today, or with a day still to come, compares nothing.
        DateOnly? since,
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

        if (since is { } day && (today is not { } local || day.AddDays(1).Day != 1 || day >= local))
            return BusinessError.Problem(BusinessError.Codes.OverviewSinceInvalid,
                "since must be a month-end before today, and today must be sent with it.");

        var dto = await overview.GetAsync(ledgerId, today, since, cancellationToken).ConfigureAwait(false);
        return Results.Ok(dto);
    }
}
