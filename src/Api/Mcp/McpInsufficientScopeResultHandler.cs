using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Coffer.Api.Mcp;

/// <summary>
/// Answers a token that lacks a scope /mcp requires with 403 <c>insufficient_scope</c>
/// naming the scope (RFC 6750 §3.1), the signal an MCP client re-authorizes on.
/// </summary>
/// <remarks>
/// Left to the default handler, a refusal is forbidden on every scheme in the policy, and
/// OpenIddict's forbid, for a token it did not authenticate, answers 401: a valid token
/// reported as invalid, which a client meets by refreshing into the same scopes again.
/// Every other outcome goes to the default handler unchanged.
/// </remarks>
public sealed class McpInsufficientScopeResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        var missing = authorizeResult.Forbidden
            ? authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<McpScopeRequirement>().FirstOrDefault()
            : null;
        if (missing is null)
            return _default.HandleAsync(next, context, policy, authorizeResult);

        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.WWWAuthenticate =
            $"Bearer error=\"insufficient_scope\", scope=\"{missing.Scope}\", "
            + $"resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource\"";
        return Task.CompletedTask;
    }
}
