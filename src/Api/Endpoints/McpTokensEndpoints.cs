using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Configuration;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Mcp;

namespace Coffer.Api.Endpoints;

/// <summary>
/// "Connected apps" — MCP access-token management (ADR-0063). The acting user
/// mints, lists, and revokes the bearer tokens an AI client uses to reach
/// <c>/mcp</c>. Interactive only (default cookie policy): a read-only MCP token
/// is not accepted here, so a token can't mint or revoke tokens. The plaintext
/// is shown exactly once, at creation. Mapped only when MCP is enabled.
/// </summary>
public static class McpTokensEndpoints
{
    public static IEndpointRouteBuilder MapMcpTokensEndpoints(this IEndpointRouteBuilder routes)
    {
        // Default policy = cookie/dev-auth (the MCP bearer scheme is deliberately
        // not in it), so these endpoints require an interactive session.
        var group = routes.MapGroup("/api/account/mcp-tokens").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapDelete("/{tokenId:guid}", RevokeAsync);
        return routes;
    }

    private static async Task<IResult> ListAsync(
        ICurrentUserAccessor currentUser,
        McpTokensRepository tokens,
        CancellationToken cancellationToken)
    {
        var list = await tokens.ListActiveAsync(currentUser.UserId, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(list);
    }

    private static async Task<IResult> CreateAsync(
        CreateMcpTokenRequest body,
        HttpContext http,
        ICurrentUserAccessor currentUser,
        StepUpGuard stepUp,
        McpTokensRepository tokens,
        IOptions<ApiOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // A token reads every ledger the user can, for up to 90 days or forever, and
        // outlives the cookie that minted it: a tap, not just a cookie (2026-10-12 audit, M2).
        var approval = await stepUp.RequireAsync(StepUpActions.McpToken, cancellationToken)
                                   .ConfigureAwait(false);
        if (approval.Failure is not null)
            return approval.Failure;

        var name = body?.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return BusinessError.Problem(BusinessError.Codes.McpTokenNameRequired,
                "A name for the connected app is required.");

        // ADR-0081 D1: read is always granted; write is opt-in and additive. The
        // scope string is authoritative — McpWriteGuard reads it back off the token.
        var scopes = body!.Writable
            ? $"{McpScopes.Read} {McpScopes.Write}"
            : McpScopes.Read;
        var lifetimeDays = options.Value.Mcp.TokenLifetimeDays;
        DateTime? expiresAt = lifetimeDays > 0
            ? clock.GetUtcNow().UtcDateTime.AddDays(lifetimeDays)
            : null;

        var (plaintext, hash) = McpTokenService.Generate();
        var id = await tokens.IssueAsync(currentUser.UserId, name, hash, scopes, expiresAt, cancellationToken)
            .ConfigureAwait(false);

        // Plaintext returned exactly once — never persisted, never retrievable again, and
        // not to be kept by any cache either.
        http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return Results.Ok(new IssuedMcpToken(id, name, scopes, expiresAt, plaintext));
    }

    private static async Task<IResult> RevokeAsync(
        Guid tokenId,
        ICurrentUserAccessor currentUser,
        McpTokensRepository tokens,
        CancellationToken cancellationToken)
    {
        var revoked = await tokens.RevokeAsync(currentUser.UserId, tokenId, cancellationToken)
            .ConfigureAwait(false);
        return revoked
            ? Results.NoContent()
            : BusinessError.Problem(BusinessError.Codes.McpTokenNotFound,
                "No active token with that id for this user.");
    }
}
