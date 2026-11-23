using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using OpenIddict.Abstractions;

using Coffer.Api.Auth;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Mcp;

/// <summary>
/// Records that an OAuth connection was used: stamps <c>oauth_connection_meta.last_used_at</c>
/// on every <c>/mcp</c> request that authenticated through OAuth, so an admin can tell a
/// connection in daily use from one nobody has touched in months.
/// </summary>
/// <remarks>
/// <para>Runs after the authorization middleware, so a request the <c>RequireMcp</c> policy
/// refused (no token, a token without <c>coffer.read</c>) never reaches it and is not a use.
/// A personal MCP token carries no authorization id and is skipped: it stamps its own row on
/// validation (<see cref="McpTokensRepository.ValidateAsync"/>).</para>
/// <para>Every request, not throttled — the same as a token. MCP traffic is an assistant
/// working for a person, not a firehose, and one cheap UPDATE per call keeps "last used"
/// exact and the two kinds of connection behaving alike.</para>
/// <para>A failed stamp is logged and swallowed: a usage record is not worth failing the
/// call it describes.</para>
/// </remarks>
public sealed class McpOAuthUsageMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<McpOAuthUsageMiddleware> _logger;

    public McpOAuthUsageMiddleware(RequestDelegate next, ILogger<McpOAuthUsageMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUserAccessor currentUser,
        OAuthConnectionMetaRepository meta,
        TimeProvider clock)
    {
        if (context.Request.Path.StartsWithSegments("/mcp")
            && currentUser.IsAuthenticated
            && context.User.GetAuthorizationId() is { Length: > 0 } authorizationId)
        {
            try
            {
                await meta.TouchAsync(
                    authorizationId, currentUser.UserId, clock.GetUtcNow().UtcDateTime, context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not record use of OAuth connection {AuthorizationId}.", authorizationId);
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
