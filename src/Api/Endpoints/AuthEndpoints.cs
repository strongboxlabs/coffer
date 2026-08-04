using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Coffer.Api.Auth;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Configuration;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Authentication endpoints that don't depend on the WebAuthn ceremony:
/// the Development-only <c>/api/auth/dev-login</c> escape hatch, the
/// scheme-agnostic <c>/api/auth/logout</c>, and <c>/api/auth/me</c>
/// (the "who am I" probe the SPA uses to gate its route-load redirect
/// to /login when the session is missing or stale).
/// </summary>
public static class AuthEndpoints
{
    /// <summary>
    /// Public DTO returned by <c>GET /api/auth/me</c>. Carries only what
    /// the SPA's header / auth-guard needs; sensitive fields like
    /// <c>created_by</c> stay behind the EF model.
    /// </summary>
    public sealed record CurrentUserResponse(
        Guid Id, string Username, string DisplayName, bool IsAdmin);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth");

        // POST /api/auth/dev-login — only registered when env=Development AND
        // Api:DevAuth=true. Mints a cookie session for the bootstrap system
        // user so cookie-auth flows are exercisable end-to-end before the
        // WebAuthn ceremonies (PR 3.4) exist. Any other deployment posture
        // refuses to register this route at all (not "registered then 403"
        // — never registered) so a misconfigured production build can't
        // accidentally serve it.
        group.MapPost("/dev-login", async (
            HttpContext http,
            SessionService sessions,
            IWebHostEnvironment env,
            IOptions<ApiOptions> options,
            CancellationToken cancellationToken) =>
            {
                if (!env.IsDevelopment() || !options.Value.DevAuth)
                    return Results.NotFound();

                var issued = await sessions.IssueAsync(
                    DevAuthHandler.SystemUserId,
                    http.Request.Headers.UserAgent.ToString(),
                    cancellationToken).ConfigureAwait(false);

                http.Response.Cookies.Append(
                    sessions.CookieName,
                    issued.CookieValue,
                    sessions.BuildCookieOptions(issued.ExpiresAt));

                return Results.Ok(new { sessionId = issued.SessionId, expiresAt = issued.ExpiresAt });
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        // GET /api/auth/me — return the authenticated user's basic
        // identity. The SPA hits this at every protected-route load to
        // decide whether to render the page or redirect to /login. The
        // cookie auth handler does all the work; the endpoint itself
        // just projects the row UsersRepository.GetByIdAsync returns
        // through RLS (users_self policy: id = current_app_user_id).
        // An anonymous caller is short-circuited to 401 by the
        // RequireAuthorization policy before this handler runs.
        group.MapGet("/me", async (
            ICurrentUserAccessor currentUser,
            UsersRepository users,
            CancellationToken cancellationToken) =>
            {
                var user = await users.GetByIdAsync(currentUser.UserId, cancellationToken)
                                      .ConfigureAwait(false);
                if (user is null)
                    // Session referenced a user_id that no longer
                    // resolves — schema invariant violation (auth_sessions
                    // CASCADE delete on user removal should prevent
                    // this). Surface as 401 so the SPA redirects to
                    // /login rather than rendering with stale state.
                    return Results.Unauthorized();

                return Results.Ok(new CurrentUserResponse(
                    Id: user.Id,
                    Username: user.Username ?? string.Empty,
                    DisplayName: user.DisplayName,
                    IsAdmin: user.IsAdmin));
            })
            .RequireAuthorization();

        // POST /api/auth/logout — revoke the current session if there is
        // one, clear the cookie unconditionally. Endpoint is anonymous by
        // design: a user with a stale cookie still wants logout to clear
        // it without a 401 round-trip.
        group.MapPost("/logout", async (
            HttpContext http,
            SessionService sessions,
            CancellationToken cancellationToken) =>
            {
                if (http.Request.Cookies.TryGetValue(sessions.CookieName, out var cookieValue))
                    await sessions.RevokeAsync(cookieValue, cancellationToken).ConfigureAwait(false);

                http.Response.Cookies.Delete(sessions.CookieName);
                return Results.Ok(new { status = "logged-out" });
            })
            .AllowAnonymous();

        return routes;
    }
}
