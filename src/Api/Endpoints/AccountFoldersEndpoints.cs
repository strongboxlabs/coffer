using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// The sidebar's folders (mig 258, FU-006). Folders are ledger data, so the routes
/// sit behind <c>RequireLedgerAccess</c>:
/// any grant reads them, owner or editor changes them, matching the role-aware RLS
/// pair on both tables.
/// </summary>
public static class AccountFoldersEndpoints
{
    public static IEndpointRouteBuilder MapAccountFoldersEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/account-folders")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{folderId:guid}", RenameAsync);
        group.MapPut("/{folderId:guid}/parent", MoveAsync);
        group.MapDelete("/{folderId:guid}", DeleteAsync);

        // Filing an account is a write about the account, so it lives under the
        // account's own path; same gate.
        var accounts = routes.MapGroup("/api/ledgers/{ledgerId:guid}/accounts")
                             .RequireAuthorization()
                             .RequireLedgerAccess();
        accounts.MapPut("/{accountId:guid}/folder", SetAccountFolderAsync);

        return routes;
    }

    private static IResult LedgerNotVisible() =>
        BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
            "Ledger not found or not visible to this user.");

    private static IResult FolderNotFound() =>
        BusinessError.Problem(BusinessError.Codes.AccountFolderNotFound,
            "Folder not found in this ledger.");

    private static IResult NameTaken() =>
        BusinessError.Problem(BusinessError.Codes.AccountFolderNameTaken,
            "A folder with that name already exists in the same place.");

    private static IResult Changed() =>
        BusinessError.Problem(BusinessError.Codes.AccountFolderChanged,
            "The folders were changed by someone else at the same time. Nothing was changed; reload and try again.");

    private static IResult? NameProblem(string? name) =>
        AccountFoldersRepository.CheckName(name) switch
        {
            AccountFoldersRepository.NameCheck.Required =>
                BusinessError.Problem(BusinessError.Codes.AccountFolderNameRequired,
                    "Folder name is required."),
            AccountFoldersRepository.NameCheck.TooLong =>
                BusinessError.Problem(BusinessError.Codes.AccountFolderNameTooLong,
                    $"Folder name is limited to {AccountFoldersRepository.NameMaxLength} characters."),
            _ => null,
        };

    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountFoldersRepository folders,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null) return LedgerNotVisible();

        var rows = await folders.ListAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateAsync(
        Guid ledgerId,
        CreateAccountFolderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountFoldersRepository folders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (NameProblem(request.Name) is { } nameProblem) return nameProblem;

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null) return LedgerNotVisible();

        var (result, folder) = await folders.CreateAsync(
            ledgerId, request.AccountType, request.Name, request.ParentId, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            AccountFoldersRepository.CreateResult.Ok =>
                Results.Created($"/api/ledgers/{ledgerId}/account-folders/{folder!.Id}", folder),
            AccountFoldersRepository.CreateResult.TypeInvalid =>
                BusinessError.Problem(BusinessError.Codes.AccountFolderTypeInvalid,
                    "A folder holds one real account type: bank, credit_card, investment, asset, liability or loan."),
            AccountFoldersRepository.CreateResult.ParentNotFound => FolderNotFound(),
            AccountFoldersRepository.CreateResult.TypeMismatch =>
                BusinessError.Problem(BusinessError.Codes.AccountFolderTypeMismatch,
                    "The parent folder holds another account type."),
            AccountFoldersRepository.CreateResult.NameTaken => NameTaken(),
            _ => Results.Problem("Unknown create result.", statusCode: 500),
        };
    }

    private static async Task<IResult> RenameAsync(
        Guid ledgerId,
        Guid folderId,
        RenameAccountFolderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountFoldersRepository folders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (NameProblem(request.Name) is { } nameProblem) return nameProblem;

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null) return LedgerNotVisible();

        var (result, folder) = await folders.RenameAsync(
            ledgerId, folderId, request.Name!, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AccountFoldersRepository.RenameResult.Ok => Results.Ok(folder),
            AccountFoldersRepository.RenameResult.NotFound => FolderNotFound(),
            AccountFoldersRepository.RenameResult.NameTaken => NameTaken(),
            _ => Results.Problem("Unknown rename result.", statusCode: 500),
        };
    }

    private static async Task<IResult> MoveAsync(
        Guid ledgerId,
        Guid folderId,
        MoveAccountFolderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountFoldersRepository folders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null) return LedgerNotVisible();

        var result = await folders.MoveAsync(ledgerId, folderId, request.ParentId, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            AccountFoldersRepository.MoveResult.Ok => Results.NoContent(),
            AccountFoldersRepository.MoveResult.NotFound or AccountFoldersRepository.MoveResult.ParentNotFound =>
                FolderNotFound(),
            AccountFoldersRepository.MoveResult.TypeMismatch =>
                BusinessError.Problem(BusinessError.Codes.AccountFolderTypeMismatch,
                    "The destination folder holds another account type."),
            AccountFoldersRepository.MoveResult.Cycle =>
                BusinessError.Problem(BusinessError.Codes.AccountFolderCycle,
                    "A folder cannot be moved into itself or one of its own sub-folders."),
            AccountFoldersRepository.MoveResult.NameTaken => NameTaken(),
            AccountFoldersRepository.MoveResult.Changed => Changed(),
            _ => Results.Problem("Unknown move result.", statusCode: 500),
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid ledgerId,
        Guid folderId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountFoldersRepository folders,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null) return LedgerNotVisible();

        var result = await folders.DeleteAsync(ledgerId, folderId, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AccountFoldersRepository.DeleteResult.Ok => Results.NoContent(),
            AccountFoldersRepository.DeleteResult.NotFound => FolderNotFound(),
            AccountFoldersRepository.DeleteResult.NameTaken =>
                BusinessError.Problem(BusinessError.Codes.AccountFolderNameTaken,
                    "A sub-folder of this folder has the same name as a folder it would move up beside. Rename one first."),
            AccountFoldersRepository.DeleteResult.Changed => Changed(),
            _ => Results.Problem("Unknown delete result.", statusCode: 500),
        };
    }

    private static async Task<IResult> SetAccountFolderAsync(
        Guid ledgerId,
        Guid accountId,
        SetAccountFolderRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountFoldersRepository folders,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null) return LedgerNotVisible();

        var result = await folders.SetAccountFolderAsync(ledgerId, accountId, request.FolderId, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            AccountFoldersRepository.SetFolderResult.Ok => Results.NoContent(),
            AccountFoldersRepository.SetFolderResult.AccountNotInLedger =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "Account does not belong to this ledger."),
            AccountFoldersRepository.SetFolderResult.AccountIsCategory =>
                BusinessError.Problem(BusinessError.Codes.AccountIsCategory,
                    "Categories are not put in folders."),
            AccountFoldersRepository.SetFolderResult.AccountIsSystem =>
                BusinessError.Problem(BusinessError.Codes.AccountIsSystem,
                    "System accounts are not put in folders."),
            AccountFoldersRepository.SetFolderResult.FolderNotFound => FolderNotFound(),
            AccountFoldersRepository.SetFolderResult.TypeMismatch =>
                BusinessError.Problem(BusinessError.Codes.AccountFolderTypeMismatch,
                    "The folder holds another account type."),
            _ => Results.Problem("Unknown set-folder result.", statusCode: 500),
        };
    }
}
