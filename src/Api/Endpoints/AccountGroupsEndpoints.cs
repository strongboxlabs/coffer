using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// User-curated sidebar tab endpoints (migration 033). Same auth
/// contract as <see cref="AccountsEndpoints"/>: authenticated user
/// plus a ledger-grant check before any row read or write.
///
/// <para>The implicit "All" tab is virtual — never returned from
/// <c>GET</c> and never accepted by mutating endpoints. The SPA
/// renders it client-side as the no-group-filter view.</para>
/// </summary>
public static class AccountGroupsEndpoints
{
    public static IEndpointRouteBuilder MapAccountGroupsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/account-groups")
                          .RequireAuthorization()
                          .RequireLedgerMembership();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{groupId:guid}", PatchAsync);
        group.MapDelete("/{groupId:guid}", DeleteAsync);
        group.MapPost("/{groupId:guid}/members/{accountId:guid}", AddMemberAsync);
        group.MapDelete("/{groupId:guid}/members/{accountId:guid}", RemoveMemberAsync);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountGroupsRepository groups,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var rows = await groups.ListAsync(currentUser.UserId, ledgerId, cancellationToken)
                               .ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateAsync(
        Guid ledgerId,
        CreateAccountGroupRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountGroupsRepository groups,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name))
            return BusinessError.Problem(BusinessError.Codes.AccountGroupNameRequired,
                "Group name is required.");

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var (result, groupId) = await groups.CreateAsync(
            currentUser.UserId, ledgerId, request.Name, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AccountGroupsRepository.CreateResult.Ok =>
                Results.Created(
                    $"/api/ledgers/{ledgerId}/account-groups/{groupId}",
                    new { id = groupId }),
            AccountGroupsRepository.CreateResult.NameConflict =>
                BusinessError.Problem(BusinessError.Codes.AccountGroupNameConflict,
                    "A group with that name already exists in this ledger."),
            _ => Results.Problem("Unknown create result.", statusCode: 500),
        };
    }

    private static async Task<IResult> PatchAsync(
        Guid ledgerId,
        Guid groupId,
        PatchAccountGroupRequest request,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountGroupsRepository groups,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Name is null)
            return BusinessError.Problem(BusinessError.Codes.AccountGroupNameRequired,
                "Group name is required (only rename is supported in this PATCH).");
        if (string.IsNullOrWhiteSpace(request.Name))
            return BusinessError.Problem(BusinessError.Codes.AccountGroupNameRequired,
                "Group name is required.");

        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var result = await groups.RenameAsync(
            currentUser.UserId, ledgerId, groupId, request.Name, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AccountGroupsRepository.RenameResult.Ok =>
                Results.NoContent(),
            AccountGroupsRepository.RenameResult.NotFound =>
                BusinessError.Problem(BusinessError.Codes.AccountGroupNotFound,
                    "Group not found in this ledger."),
            AccountGroupsRepository.RenameResult.NameConflict =>
                BusinessError.Problem(BusinessError.Codes.AccountGroupNameConflict,
                    "A group with that name already exists in this ledger."),
            _ => Results.Problem("Unknown rename result.", statusCode: 500),
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid ledgerId,
        Guid groupId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountGroupsRepository groups,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var result = await groups.DeleteAsync(
            currentUser.UserId, ledgerId, groupId, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AccountGroupsRepository.DeleteResult.Ok =>
                Results.NoContent(),
            AccountGroupsRepository.DeleteResult.NotFound =>
                BusinessError.Problem(BusinessError.Codes.AccountGroupNotFound,
                    "Group not found in this ledger."),
            _ => Results.Problem("Unknown delete result.", statusCode: 500),
        };
    }

    private static async Task<IResult> AddMemberAsync(
        Guid ledgerId,
        Guid groupId,
        Guid accountId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountGroupsRepository groups,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var result = await groups.AddMemberAsync(
            currentUser.UserId, ledgerId, groupId, accountId, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AccountGroupsRepository.AddMemberResult.Ok =>
                Results.NoContent(),
            AccountGroupsRepository.AddMemberResult.GroupNotFound =>
                BusinessError.Problem(BusinessError.Codes.AccountGroupNotFound,
                    "Group not found in this ledger."),
            AccountGroupsRepository.AddMemberResult.AccountNotInLedger =>
                BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                    "Account does not belong to this ledger."),
            _ => Results.Problem("Unknown add-member result.", statusCode: 500),
        };
    }

    private static async Task<IResult> RemoveMemberAsync(
        Guid ledgerId,
        Guid groupId,
        Guid accountId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountGroupsRepository groups,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var result = await groups.RemoveMemberAsync(
            currentUser.UserId, ledgerId, groupId, accountId, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            AccountGroupsRepository.RemoveMemberResult.Ok =>
                Results.NoContent(),
            AccountGroupsRepository.RemoveMemberResult.GroupNotFound =>
                BusinessError.Problem(BusinessError.Codes.AccountGroupNotFound,
                    "Group not found in this ledger."),
            _ => Results.Problem("Unknown remove-member result.", statusCode: 500),
        };
    }
}
