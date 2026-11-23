using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Errors;
using Coffer.Api.Mcp;

namespace Coffer.Api.Endpoints;

/// <summary>
/// The apps a user connected through OAuth, beside their MCP tokens on Account → Security
/// (FU-087). Each grant reads every ledger the user can see, so the user must be able to
/// see it, name it and end it — not only an admin removing the whole client, for every
/// user at once.
/// </summary>
/// <remarks>
/// Interactive only, like the MCP tokens: the default cookie policy, so an MCP credential
/// can neither list nor end grants. OpenIddict's tables have no row-level security (its
/// stores run where no user is signed in), so <see cref="OAuthConnectionsService"/> matches the
/// subject on every read and on the rename and revoke. Mapped only when MCP is enabled.
/// </remarks>
public static class OAuthGrantsEndpoints
{
    /// <param name="AppName">What the client registered itself as.</param>
    /// <param name="Label">The user's name for this connection, or null.</param>
    /// <param name="LastUsedAt">Null when no use has been recorded: never used, or a connection
    /// that predates the record.</param>
    public sealed record OAuthGrantSummary(
        string Id, string AppName, bool CanWrite, DateTimeOffset ConnectedAt, DateTimeOffset ExpiresAt,
        string? Label, DateTime? LastUsedAt);

    public static IEndpointRouteBuilder MapOAuthGrantsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/account/oauth-grants").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPatch("/{grantId}", RenameAsync);
        group.MapDelete("/{grantId}", RevokeAsync);
        return routes;
    }

    private static async Task<IResult> ListAsync(
        ICurrentUserAccessor currentUser,
        OAuthConnectionsService connections,
        CancellationToken cancellationToken)
    {
        var mine = await connections.ListAsync(currentUser.UserId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(mine.Select(c => new OAuthGrantSummary(
            c.Id, c.AppName, c.CanWrite, c.ConnectedAt, c.ExpiresAt, c.Label, c.LastUsedAt)).ToList());
    }

    private static async Task<IResult> RenameAsync(
        string grantId,
        RenameConnectionRequest request,
        ICurrentUserAccessor currentUser,
        OAuthConnectionsService connections,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Someone else's grant answers exactly as a missing one does: its existence is
        // not this user's to learn.
        return await connections.RenameAsync(currentUser.UserId, grantId, request.Name, cancellationToken)
                                .ConfigureAwait(false)
            ? Results.NoContent()
            : NotFound();
    }

    private static async Task<IResult> RevokeAsync(
        string grantId,
        ICurrentUserAccessor currentUser,
        OAuthConnectionsService connections,
        CancellationToken cancellationToken)
    {
        var revoked = await connections.RevokeAsync(currentUser.UserId, grantId, cancellationToken)
                                       .ConfigureAwait(false);
        return revoked is null ? NotFound() : Results.NoContent();
    }

    private static IResult NotFound() =>
        BusinessError.Problem(BusinessError.Codes.OAuthGrantNotFound,
            "No connected app with that id for this user.");
}
