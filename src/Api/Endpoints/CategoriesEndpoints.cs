using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger category-management endpoints (Slice A) — the REST surface for the
/// manage-categories UI, over the ADR-0068 repository methods that previously had
/// only an MCP consumer. Same auth contract as <see cref="AccountsEndpoints"/>:
/// authenticated user + a grant on the ledger; RLS enforces the same predicate
/// at the data layer. Categories are accounts (<c>account_type='category'</c>);
/// create / rename / activate reuse the accounts endpoints, so this file only
/// carries the hierarchy-management operations (list-with-usage, reparent, merge,
/// delete).
/// </summary>
public static class CategoriesEndpoints
{
    public static IEndpointRouteBuilder MapCategoriesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/categories")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapGet("/", ListAsync);
        group.MapPatch("/{categoryId:guid}/parent", ReparentAsync);
        group.MapPost("/{categoryId:guid}/merge", MergeAsync);
        group.MapDelete("/{categoryId:guid}", DeleteAsync);
        return routes;
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/categories</c> — the management tree source:
    /// every category with its hierarchy pointer + usage counts (txn legs + child
    /// categories). <c>?includeInactive=true</c> includes deactivated categories.
    /// </summary>
    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CancellationToken cancellationToken,
        bool? includeInactive = null)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var rows = await accounts.ListCategoriesWithUsageAsync(
            ledgerId, includeInactive ?? false, cancellationToken).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    /// <summary>
    /// <c>PATCH /api/ledgers/{ledgerId}/categories/{categoryId}/parent</c> — move a
    /// category under a new parent (<c>parentId: null</c> = top level). The parent
    /// must be a category of the same ledger; cycles are rejected. A no-op move
    /// (already under that parent) is treated as success.
    /// </summary>
    private static async Task<IResult> ReparentAsync(
        Guid ledgerId,
        Guid categoryId,
        ReparentCategoryRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var result = await accounts.ReparentCategoryAsync(
            ledgerId, categoryId, request.ParentId, dryRun: false, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            AccountsRepository.ReparentCategoryResult.Ok => Results.NoContent(),
            AccountsRepository.ReparentCategoryResult.SameParent => Results.NoContent(),
            AccountsRepository.ReparentCategoryResult.NotInLedger =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "Category does not belong to this ledger."),
            AccountsRepository.ReparentCategoryResult.NotCategory =>
                BusinessError.Problem(BusinessError.Codes.AccountNotACategory,
                    "Only categories can be reparented."),
            AccountsRepository.ReparentCategoryResult.IsSystem =>
                BusinessError.Problem(BusinessError.Codes.AccountIsSystem,
                    "System categories cannot be moved."),
            AccountsRepository.ReparentCategoryResult.ParentNotCategory =>
                BusinessError.Problem(BusinessError.Codes.AccountParentInvalid,
                    "The new parent must be a category in this ledger."),
            AccountsRepository.ReparentCategoryResult.WouldCycle =>
                BusinessError.Problem(BusinessError.Codes.CategoryCycle,
                    "That move would make a category its own ancestor."),
            _ => Results.Problem("Unknown reparent-category result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/categories/{categoryId}/merge</c> — merge this
    /// (source) category into <c>targetId</c>: repoint every leg (committed +
    /// reminder templates), reparent the source's children to the target, and
    /// deactivate the source (reversible). Both must be the same kind. <c>dryRun</c>
    /// returns the counts that would move without writing.
    /// </summary>
    private static async Task<IResult> MergeAsync(
        Guid ledgerId,
        Guid categoryId,
        MergeCategoryRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TargetId == Guid.Empty)
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "A target category is required.");

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await accounts.MergeCategoryAsync(
            ledgerId, categoryId, request.TargetId, request.DryRun, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Result switch
        {
            AccountsRepository.MergeCategoryResult.Ok =>
                Results.Ok(new MergeCategoryResponse(
                    outcome.TransactionsMoved, outcome.ChildrenReparented, request.DryRun)),
            AccountsRepository.MergeCategoryResult.SourceNotInLedger =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "Source category does not belong to this ledger."),
            AccountsRepository.MergeCategoryResult.TargetNotInLedger =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "Target category does not belong to this ledger."),
            AccountsRepository.MergeCategoryResult.NotCategory =>
                BusinessError.Problem(BusinessError.Codes.AccountNotACategory,
                    "Both source and target must be categories."),
            AccountsRepository.MergeCategoryResult.KindMismatch =>
                BusinessError.Problem(BusinessError.Codes.CategoryKindMismatch,
                    "Categories can only merge into another of the same kind (income or expense)."),
            AccountsRepository.MergeCategoryResult.SameCategory =>
                BusinessError.Problem(BusinessError.Codes.CategoryMergeSelf,
                    "A category cannot be merged into itself."),
            AccountsRepository.MergeCategoryResult.SourceIsSystem =>
                BusinessError.Problem(BusinessError.Codes.AccountIsSystem,
                    "System categories cannot be merged away."),
            _ => Results.Problem("Unknown merge-category result.", statusCode: 500),
        };
    }

    /// <summary>
    /// <c>DELETE /api/ledgers/{ledgerId}/categories/{categoryId}</c> — hard-delete a
    /// category, allowed only when it has zero referencing legs and zero children
    /// and is not system-managed; otherwise <c>category-in-use</c> (merge it first).
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid ledgerId,
        Guid categoryId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var outcome = await accounts.DeleteCategoryAsync(
            ledgerId, categoryId, dryRun: false, cancellationToken).ConfigureAwait(false);
        return outcome.Result switch
        {
            AccountsRepository.DeleteCategoryResult.Ok => Results.NoContent(),
            AccountsRepository.DeleteCategoryResult.NotInLedger =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "Category does not belong to this ledger."),
            AccountsRepository.DeleteCategoryResult.NotCategory =>
                BusinessError.Problem(BusinessError.Codes.AccountNotACategory,
                    "Only categories can be deleted here."),
            AccountsRepository.DeleteCategoryResult.IsSystem =>
                BusinessError.Problem(BusinessError.Codes.AccountIsSystem,
                    "System categories cannot be deleted."),
            AccountsRepository.DeleteCategoryResult.InUse =>
                BusinessError.Problem(BusinessError.Codes.CategoryInUse,
                    $"Category still has {outcome.TransactionCount} transaction(s) and "
                    + $"{outcome.ChildCount} sub-categor{(outcome.ChildCount == 1 ? "y" : "ies")}; "
                    + "merge it into another category instead."),
            _ => Results.Problem("Unknown delete-category result.", statusCode: 500),
        };
    }
}
