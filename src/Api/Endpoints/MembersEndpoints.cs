using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Ledger membership management (ADR-0083): list members, change a member's role,
/// remove a member — all under <c>/api/ledgers/{ledgerId}/members</c>. Listing is
/// any-member (read); mutations are owner-only (<see cref="LedgerAccessExtensions.AsLedgerOwner"/>)
/// and enforce the ≥1-owner invariant in the repository. Grant writes run as the
/// service role there (<c>user_ledger_grants</c> is SELECT-only for coffer_app).
/// Adding a NEW member is the invite flow (ADR-0083 slice B), not here.
/// </summary>
public static class MembersEndpoints
{
    public static IEndpointRouteBuilder MapMembersEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/members")
                          .RequireAuthorization()
                          .RequireLedgerAccess();

        group.MapGet("/", ListAsync);
        group.MapPut("/{userId:guid}", SetRoleAsync).AsLedgerOwner();
        group.MapDelete("/{userId:guid}", RemoveAsync).AsLedgerOwner();

        return routes;
    }

    private static async Task<IResult> ListAsync(
        Guid ledgerId, LedgersRepository ledgers, CancellationToken cancellationToken) =>
        Results.Ok(await ledgers.ListMembersAsync(ledgerId, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SetRoleAsync(
        Guid ledgerId, Guid userId, SetMemberRoleRequest request,
        LedgersRepository ledgers, CancellationToken cancellationToken) =>
        Map(await ledgers.SetMemberRoleAsync(ledgerId, userId, request.Role?.Trim() ?? "", cancellationToken)
                         .ConfigureAwait(false));

    private static async Task<IResult> RemoveAsync(
        Guid ledgerId, Guid userId,
        LedgersRepository ledgers, CancellationToken cancellationToken) =>
        Map(await ledgers.RemoveMemberAsync(ledgerId, userId, cancellationToken).ConfigureAwait(false));

    private static IResult Map(LedgersRepository.MemberChangeResult result) => result switch
    {
        LedgersRepository.MemberChangeResult.Ok => Results.NoContent(),
        LedgersRepository.MemberChangeResult.InvalidRole => BusinessError.Problem(
            BusinessError.Codes.MemberInvalidRole, "Role must be owner, editor, or viewer."),
        LedgersRepository.MemberChangeResult.NotAMember => BusinessError.Problem(
            BusinessError.Codes.MemberNotFound,
            "That user is not a member of this ledger. Invite them first."),
        LedgersRepository.MemberChangeResult.LastOwner => BusinessError.Problem(
            BusinessError.Codes.MemberLastOwner,
            "A ledger must keep at least one owner. Make someone else an owner first."),
        LedgersRepository.MemberChangeResult.SystemUser => BusinessError.Problem(
            BusinessError.Codes.MemberSystemUser,
            "The system account can't be changed or removed."),
        _ => BusinessError.Problem(BusinessError.Codes.MemberNotFound, "Unable to update the member."),
    };
}
