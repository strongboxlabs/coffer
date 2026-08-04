using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Payee suggestions for the register's payee-edit typeahead. One
/// route, one shape: the SPA caches the list aggressively (TanStack
/// Query staleTime ~30 s) and filters client-side. No prefix-search
/// endpoint — for a personal-finance ledger this is a few hundred
/// strings, which fits comfortably in memory and dodges per-keystroke
/// roundtrips.
/// </summary>
public static class PayeesEndpoints
{
    public static IEndpointRouteBuilder MapPayeesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/payees")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapGet("/", ListAsync);

        return routes;
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/payees</c>. Returns the distinct
    /// resolved payees in the ledger, ranked by usage count then
    /// recency. Hidden + merged headers are excluded.
    /// </summary>
    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        PayeesRepository payees,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var suggestions = await payees.ListByLedgerAsync(ledgerId, cancellationToken)
                                      .ConfigureAwait(false);
        return Results.Ok(suggestions);
    }
}
