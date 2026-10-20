using Coffer.Api.Auth;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Quotes;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Endpoints for the quote-provider family (ADR-0033). Mirrors
/// the per-family parallel structure — separate router file per
/// family, separate orchestrator dependency.
/// </summary>
public static class QuotesEndpoints
{
    public static IEndpointRouteBuilder MapQuotesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/quotes")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        // POST /api/ledgers/{ledgerId}/quotes/refresh
        // — runs every registered pull-capable quote provider for
        // the ledger. Today: just SimpleFinHoldingsQuoteProvider
        // (which reads stored raw payloads, no external HTTP).
        // When Yahoo / other pull providers land they slot in via
        // DI registration; no endpoint change needed.
        group.MapPost("/refresh", RefreshAsync);

        return routes;
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/quotes/refresh</c> — fan
    /// out to every registered <see cref="IQuotePullProvider"/>.
    /// Returns the typed <see cref="QuoteRunOutcome"/> with
    /// per-provider counts + unresolved securities + errors.
    ///
    /// 422 cases: <c>ledger-not-visible</c>.
    /// </summary>
    private static async Task<IResult> RefreshAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        QuoteOrchestrator quotes,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await quotes.RunAllPullsAsync(
            ledgerId, "manual", currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(outcome);
    }
}
