using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Admin user management (ADR-0083): list users, disable/enable, grant/revoke the
/// instance admin flag — under <c>/api/admin/users</c>, gated by
/// <see cref="AuthPolicies.RequireAdmin"/> (ADR-0060). Writes run as the service role
/// in the repository (<c>is_admin</c>/<c>is_disabled</c> are service-only columns) and
/// enforce the ≥1-enabled-admin invariant so no action locks every administrator out.
/// Inviting a NEW user is the invite flow (ADR-0083 slice B), not here.
/// </summary>
public static class AdminUsersEndpoints
{
    public static IEndpointRouteBuilder MapAdminUsersEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/users")
                          .RequireAuthorization(AuthPolicies.RequireAdmin);

        group.MapGet("/", ListAsync);
        group.MapPut("/{userId:guid}/disabled", SetDisabledAsync);
        group.MapPut("/{userId:guid}/admin", SetAdminAsync);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        UsersRepository users, CancellationToken cancellationToken) =>
        Results.Ok(await users.ListAllAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SetDisabledAsync(
        Guid userId, SetUserDisabledRequest request,
        UsersRepository users, CancellationToken cancellationToken) =>
        Map(await users.SetDisabledAsync(userId, request.Disabled, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SetAdminAsync(
        Guid userId, SetUserAdminRequest request,
        UsersRepository users, CancellationToken cancellationToken) =>
        Map(await users.SetAdminAsync(userId, request.IsAdmin, cancellationToken).ConfigureAwait(false));

    private static IResult Map(UsersRepository.AdminUserChangeResult result) => result switch
    {
        UsersRepository.AdminUserChangeResult.Ok => Results.NoContent(),
        UsersRepository.AdminUserChangeResult.NotFound => BusinessError.Problem(
            BusinessError.Codes.UserNotFound, "No such user."),
        UsersRepository.AdminUserChangeResult.LastAdmin => BusinessError.Problem(
            BusinessError.Codes.UserLastAdmin,
            "The instance must keep at least one enabled admin. Grant another user admin first."),
        _ => BusinessError.Problem(BusinessError.Codes.UserNotFound, "Unable to update the user."),
    };
}
