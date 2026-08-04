using System.Linq;
using System.Security.Claims;

using Microsoft.AspNetCore.Http;

namespace Coffer.Api.Auth;

/// <summary>
/// Resolves the authenticated user's id from <see cref="HttpContext"/>.
/// Endpoint code that needs the current user takes <see cref="ICurrentUserAccessor"/>
/// from DI rather than poking <c>HttpContext.User</c> directly so the lookup
/// rule lives in exactly one place and is easy to mock.
/// </summary>
public interface ICurrentUserAccessor
{
    /// <summary>
    /// True iff an authenticated principal with a parseable user-id
    /// claim is on the current <see cref="HttpContext"/>. Used by the
    /// RLS connection interceptor to decide whether to <c>SET app.user_id</c>;
    /// pre-auth code paths (cookie validator, login /begin) return
    /// false and the interceptor leaves the GUC unset (RLS denies).
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// The authenticated user's id (from the <c>NameIdentifier</c> claim).
    /// Throws when no authenticated principal is present — callers reach
    /// this only behind <c>RequireAuthorization</c>, so an unauthenticated
    /// request would be a programming error rather than user input.
    /// </summary>
    Guid UserId { get; }
}

internal sealed class HttpContextCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public bool IsAuthenticated => TryResolve(out _);

    public Guid UserId
    {
        get
        {
            if (!TryResolve(out var userId))
                throw new InvalidOperationException(
                    "Authenticated principal has no parseable NameIdentifier claim. " +
                    "ICurrentUserAccessor.UserId is only valid behind RequireAuthorization; " +
                    "pre-auth code paths should check IsAuthenticated instead.");
            return userId;
        }
    }

    private bool TryResolve(out Guid userId)
    {
        userId = Guid.Empty;
        var principal = _httpContextAccessor.HttpContext?.User;
        // Check every identity, not just the primary one: the /mcp policy lists
        // several auth schemes, and the authenticated identity may not be the
        // primary after the middleware merges them.
        if (principal is null || !principal.Identities.Any(i => i.IsAuthenticated))
            return false;
        // Cookie / dev-auth / MCP-bearer principals carry the user id in
        // NameIdentifier; OAuth access tokens (OpenIddict) carry it as the
        // subject ("sub"). Accept either so RLS is set identically regardless
        // of how the request authenticated (ADR-0063).
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out userId);
    }
}
