using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Admin user management (ADR-0083): list users, disable/enable, grant/revoke the
/// instance admin flag, sign a user out everywhere — under <c>/api/admin/users</c>,
/// gated by <see cref="AuthPolicies.RequireAdmin"/> (ADR-0060). Writes run as the
/// service role in the repository (<c>is_admin</c>/<c>is_disabled</c> are service-only
/// columns) and enforce the ≥1-enabled-admin invariant so no action locks every
/// administrator out. Inviting a NEW user is the invite flow (ADR-0083 slice B), not here.
/// </summary>
/// <remarks>
/// <para><b>Disabling ends access</b> (2026-10-12 audit, H2). It used to block new logins
/// only: open sessions, MCP tokens and OAuth grants all kept working, and a disabled
/// admin could re-enable themselves. Disabling now revokes all of them, and re-enabling
/// restores none: the user signs in again and reconnects their apps.</para>
///
/// <para><b>Granting admin needs a passkey tap</b> (M2): it is the widest grant there is.
/// Revoking admin, disabling, and signing out are protective and need only the session.</para>
/// </remarks>
public static class AdminUsersEndpoints
{
    public static IEndpointRouteBuilder MapAdminUsersEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/users")
                          .RequireAuthorization(AuthPolicies.RequireAdmin);

        group.MapGet("/", ListAsync);
        group.MapPut("/{userId:guid}/disabled", SetDisabledAsync);
        group.MapPut("/{userId:guid}/admin", SetAdminAsync);
        group.MapPost("/{userId:guid}/sign-out", SignOutEverywhereAsync);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        UsersRepository users, CancellationToken cancellationToken) =>
        Results.Ok(await users.ListAllAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SetDisabledAsync(
        Guid userId, SetUserDisabledRequest request,
        UsersRepository users, UserAccessRevoker revoker, InvitesRepository invites,
        AdminAuditRepository audit, ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var result = await users.SetDisabledAsync(userId, request.Disabled, cancellationToken).ConfigureAwait(false);
        if (result == UsersRepository.AdminUserChangeResult.Ok && request.Disabled)
        {
            await revoker.RevokeEverywhereAsync(userId, cancellationToken).ConfigureAwait(false);
            await invites.DeleteUnauthorizedIssuedByAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        if (result == UsersRepository.AdminUserChangeResult.Ok)
            await audit.AppendAsync(
                request.Disabled ? AdminAuditActions.UserDisabled : AdminAuditActions.UserEnabled,
                currentUser.UserId, $"user {userId}", cancellationToken).ConfigureAwait(false);
        return Map(result);
    }

    private static async Task<IResult> SetAdminAsync(
        Guid userId, SetUserAdminRequest request,
        UsersRepository users, StepUpGuard stepUp, InvitesRepository invites,
        AdminAuditRepository audit, ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        if (request.IsAdmin)
        {
            var approval = await stepUp.RequireAsync(StepUpActions.GrantAdmin, cancellationToken)
                                       .ConfigureAwait(false);
            if (approval.Failure is not null)
                return approval.Failure;
        }

        var result = await users.SetAdminAsync(userId, request.IsAdmin, cancellationToken).ConfigureAwait(false);
        if (result == UsersRepository.AdminUserChangeResult.Ok && !request.IsAdmin)
            // Their admin-granting invites (and ledger invites they could only send as an
            // admin) die with the role.
            await invites.DeleteUnauthorizedIssuedByAsync(userId, cancellationToken).ConfigureAwait(false);
        if (result == UsersRepository.AdminUserChangeResult.Ok)
            await audit.AppendAsync(
                request.IsAdmin ? AdminAuditActions.UserAdminGranted : AdminAuditActions.UserAdminRevoked,
                currentUser.UserId, $"user {userId}", cancellationToken).ConfigureAwait(false);
        return Map(result);
    }

    private static async Task<IResult> SignOutEverywhereAsync(
        Guid userId, UsersRepository users, UserAccessRevoker revoker,
        CancellationToken cancellationToken)
    {
        if (!await users.ExistsAsync(userId, cancellationToken).ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.UserNotFound, "No such user.");
        var revoked = await revoker.RevokeEverywhereAsync(userId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new SignOutEverywhereResponse(
            revoked.Sessions, revoked.McpTokens, revoked.OAuthAuthorizations));
    }

    private static IResult Map(UsersRepository.AdminUserChangeResult result) => result switch
    {
        UsersRepository.AdminUserChangeResult.Ok or UsersRepository.AdminUserChangeResult.Unchanged
            => Results.NoContent(),
        UsersRepository.AdminUserChangeResult.NotFound => BusinessError.Problem(
            BusinessError.Codes.UserNotFound, "No such user."),
        UsersRepository.AdminUserChangeResult.LastAdmin => BusinessError.Problem(
            BusinessError.Codes.UserLastAdmin,
            "The instance must keep at least one enabled admin. Grant another user admin first."),
        _ => BusinessError.Problem(BusinessError.Codes.UserNotFound, "Unable to update the user."),
    };
}

/// <summary>
/// Response to <c>POST /api/admin/users/{id}/sign-out</c>: what was ended.
/// </summary>
public sealed record SignOutEverywhereResponse(int SessionsEnded, int McpTokensRevoked, long AppConnectionsRevoked);
