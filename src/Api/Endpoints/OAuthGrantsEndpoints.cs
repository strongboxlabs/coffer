using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using OpenIddict.Abstractions;

using Coffer.Api.Auth;
using Coffer.Api.Errors;
using Coffer.Api.Mcp;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Coffer.Api.Endpoints;

/// <summary>
/// The apps a user connected through OAuth, beside their MCP tokens on Account → Security
/// (FU-087). Each grant reads every ledger the user can see, and until now nothing showed
/// it to the user or let them end it: the only way was an admin removing the whole client,
/// for every user at once.
/// </summary>
/// <remarks>
/// Interactive only, like the MCP tokens: the default cookie policy, so an MCP credential
/// can neither list nor end grants. OpenIddict's tables have no row-level security (its
/// stores run where no user is signed in), so the subject is matched here, on every read
/// and on the revoke. Mapped only when MCP is enabled.
/// </remarks>
public static class OAuthGrantsEndpoints
{
    public sealed record OAuthGrantSummary(
        string Id, string AppName, bool CanWrite, DateTimeOffset ConnectedAt, DateTimeOffset ExpiresAt);

    public static IEndpointRouteBuilder MapOAuthGrantsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/account/oauth-grants").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapDelete("/{grantId}", RevokeAsync);
        return routes;
    }

    private static async Task<IResult> ListAsync(
        ICurrentUserAccessor currentUser,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictApplicationManager applications,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var grants = new List<OAuthGrantSummary>();
        await foreach (var authorization in authorizations
                           .FindBySubjectAsync(currentUser.UserId.ToString(), cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!await authorizations.HasStatusAsync(authorization, Statuses.Valid, cancellationToken).ConfigureAwait(false))
                continue;
            var created = await authorizations.GetCreationDateAsync(authorization, cancellationToken).ConfigureAwait(false);
            // Past its age it is already over: the token endpoint refuses it.
            if (OAuthGrants.IsExpired(created, now)) continue;

            var applicationId = await authorizations.GetApplicationIdAsync(authorization, cancellationToken).ConfigureAwait(false);
            var application = applicationId is null
                ? null
                : await applications.FindByIdAsync(applicationId, cancellationToken).ConfigureAwait(false);
            var name = application is null
                ? "Removed app"
                : await applications.GetDisplayNameAsync(application, cancellationToken).ConfigureAwait(false)
                  ?? await applications.GetClientIdAsync(application, cancellationToken).ConfigureAwait(false)
                  ?? "Unnamed app";
            var scopes = await authorizations.GetScopesAsync(authorization, cancellationToken).ConfigureAwait(false);

            grants.Add(new OAuthGrantSummary(
                (await authorizations.GetIdAsync(authorization, cancellationToken).ConfigureAwait(false))!,
                name,
                scopes.Contains(McpScopes.Write, StringComparer.Ordinal),
                created!.Value,
                created.Value + OAuthGrants.MaxAge));
        }
        return Results.Ok(grants.OrderByDescending(g => g.ConnectedAt).ToList());
    }

    private static async Task<IResult> RevokeAsync(
        string grantId,
        ICurrentUserAccessor currentUser,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictTokenManager tokens,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizations.FindByIdAsync(grantId, cancellationToken).ConfigureAwait(false);
        // Someone else's grant answers exactly as a missing one does: its existence is
        // not this user's to learn.
        if (authorization is null
            || !string.Equals(
                await authorizations.GetSubjectAsync(authorization, cancellationToken).ConfigureAwait(false),
                currentUser.UserId.ToString(), StringComparison.OrdinalIgnoreCase)
            || !await authorizations.HasStatusAsync(authorization, Statuses.Valid, cancellationToken).ConfigureAwait(false))
            return BusinessError.Problem(BusinessError.Codes.OAuthGrantNotFound,
                "No connected app with that id for this user.");

        await OAuthGrants.RevokeAsync(authorizations, tokens, authorization, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }
}
