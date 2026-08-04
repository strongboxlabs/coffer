using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger balance diagnostic surface. Exists because the
/// stored-running-balance scheme (txn_header_account_balances,
/// written by <c>fn_recompute_balances_for_account</c> via the
/// interceptor) is non-trivial to audit by eye — if ANY writer
/// of a balance-relevant field skips the interceptor, the
/// stored value drifts from the canonical recompute and the
/// SPA register shows a wrong number forever. This endpoint
/// is the explicit verify-and-heal lever for that class of bug.
/// </summary>
public static class BalancesEndpoints
{
    public static IEndpointRouteBuilder MapBalancesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/balances")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        // Verify + heal — snapshot all balance rows, run the
        // canonical recompute for every account in the ledger,
        // diff. The recompute is the heal step; the diff is
        // the report. It MUTATES (rewrites txn_header_account_balances),
        // so it requires write access (the RequireLedgerAccess default for a
        // POST) — NOT AsLedgerRead, under which a viewer's heal would be
        // silently no-op'd by RLS and still report "ok".
        group.MapPost("/health", HealthAsync);

        return routes;
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/balances/health</c>. Verify the
    /// stored running balance for every header row in the ledger
    /// against a fresh canonical recompute, returning the rows that
    /// drifted. The recompute side-effect heals any drift in the
    /// same call — so a non-empty drift list means "there WAS drift
    /// (logged here for diagnosis), and it has now been corrected".
    /// </summary>
    private static async Task<IResult> HealthAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        RegisterRepository register,
        CancellationToken cancellationToken)
    {
        var visibleLedger = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visibleLedger is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var report = await register.VerifyAndHealBalancesAsync(
            ledgerId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(report);
    }
}
