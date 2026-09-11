using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
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

        // Same gate the transaction write endpoints use — RequireLedgerAccess is the
        // repo's only ledger gate, and TransactionsEndpoints writes under it. Declared
        // as its own group purely because MapGet/MapPost cannot chain off one builder.
        routes.MapGroup("/api/ledgers/{ledgerId:guid}/ledger-operations")
              .RequireAuthorization()
              .RequireLedgerAccess()
              .MapPost("/{operationId:guid}/undo-import", UndoImportAsync);
        return routes;
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/ledger-operations/{operationId}/undo-import?dryRun=</c>
    /// — remove every transaction that import created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what file imports have INSTEAD of dedup. CSV carries no issuer-assigned
    /// row id, so matching would have to be inferred from content — which cannot tell
    /// two identical transactions apart, and gets it silently wrong in both directions.
    /// An exact undo needs no inference at all.
    /// </para>
    /// <para>
    /// <c>dryRun=true</c> counts without deleting, so the confirm dialog can say how
    /// many rows will go and how many of them the user has edited since. Deleting money
    /// on an unconfirmed click is not a thing this endpoint should make easy.
    /// </para>
    /// <para>
    /// Only <c>family='ingest'</c> operations are undoable. A quote refresh created no
    /// transactions, so "undo" on one is a request that cannot mean anything — refused
    /// by name rather than quietly returning zero, which would read as success.
    /// </para>
    /// </remarks>
    private static async Task<IResult> UndoImportAsync(
        Guid ledgerId,
        Guid operationId,
        bool? dryRun,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        LedgerOperationsRepository runs,
        BulkTransactionsRepository bulk,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var family = await runs.FamilyForAsync(ledgerId, operationId, cancellationToken)
            .ConfigureAwait(false);
        if (family is null)
            return BusinessError.Problem("operation-not-found",
                "No such operation on this ledger.");
        if (!string.Equals(family, "ingest", StringComparison.Ordinal))
            return BusinessError.Problem("operation-not-an-import",
                "Only an import can be undone; this operation is '" + family + "'.");

        var result = await bulk.UndoImportAsync(
            ledgerId, operationId, dryRun ?? false, cancellationToken).ConfigureAwait(false);

        if (result.TooLarge)
            return BusinessError.Problem("import-too-large-to-undo",
                "This import created " + result.Found + " transactions, above the "
                + SelectionLimits.MaxIds + " the bulk path handles in one call. "
                + "Nothing was deleted — a partly-undone import is worse than none.");

        return Results.Ok(result);
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
