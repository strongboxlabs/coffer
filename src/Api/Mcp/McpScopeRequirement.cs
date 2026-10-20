using Microsoft.AspNetCore.Authorization;

namespace Coffer.Api.Mcp;

/// <summary>
/// The caller was granted <see cref="Scope"/> (FU-095). Its own requirement type, not a
/// <c>RequireAssertion</c>, so <see cref="McpInsufficientScopeResultHandler"/> can tell a
/// missing scope from any other refusal and answer it as one.
/// </summary>
public sealed class McpScopeRequirement : AuthorizationHandler<McpScopeRequirement>, IAuthorizationRequirement
{
    public McpScopeRequirement(string scope) => Scope = scope;

    public string Scope { get; }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, McpScopeRequirement requirement)
    {
        if (McpScopes.Has(context.User, requirement.Scope))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
