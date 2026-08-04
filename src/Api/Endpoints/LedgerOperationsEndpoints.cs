using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Ledger-wide provider-activity timeline (ADR-0055 slice C). Reads every
/// <c>ledger_operations</c> row across families (ingest + quote), filterable by
/// provider + recency. The per-connection ingest log stays at <c>/sync-runs</c>.
/// </summary>
public static class LedgerOperationsEndpoints
{
    /// <summary>Default page size when the client omits <c>limit</c>.</summary>
    public const int DefaultLimit = 100;

    public static IEndpointRouteBuilder MapLedgerOperationsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGroup("/api/ledgers/{ledgerId:guid}/ledger-operations")
              .RequireAuthorization()
              .RequireLedgerAccess()
              .MapGet("/", ListAsync);
        return routes;
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/ledger-operations?provider=&amp;days=&amp;limit=</c>
    /// — recent runs across all provider families, newest first. <c>provider</c>
    /// filters to one provider_key (omit = all); <c>days</c> limits to runs
    /// started in the last N days (omit = all). 422 on <c>ledger-not-visible</c>.
    /// </summary>
    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        string? provider,
        int? days,
        int? limit,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerOperationsRepository runs,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        DateTime? sinceUtc = days is { } d && d > 0
            ? DateTime.UtcNow.AddDays(-d)
            : null;
        var rows = await runs.ListByLedgerAsync(
            ledgerId, provider, sinceUtc, limit ?? DefaultLimit, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(rows);
    }
}
