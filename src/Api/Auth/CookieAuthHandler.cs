using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Coffer.Api.Auth.Webauthn;

namespace Coffer.Api.Auth;

/// <summary>
/// AuthenticationHandler that reads the session cookie set by
/// <see cref="SessionService"/>, validates it against <c>auth_sessions</c>,
/// and surfaces the authenticated user via <see cref="HttpContext.User"/>.
/// The cookie value never leaves this handler — endpoints get a
/// <see cref="ClaimsPrincipal"/> and use <see cref="ICurrentUserAccessor"/>
/// to pull the user id.
/// </summary>
public sealed class CookieAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly SessionService _sessions;

    public CookieAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        SessionService sessions)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(_sessions.CookieName, out var cookieValue) ||
            string.IsNullOrEmpty(cookieValue))
        {
            // No cookie: not "no result" so other schemes get a chance, just
            // "this scheme didn't authenticate." NoResult signals that to the
            // authentication middleware.
            return AuthenticateResult.NoResult();
        }

        var validated = await _sessions.ValidateAsync(cookieValue, Context.RequestAborted)
                                       .ConfigureAwait(false);
        if (validated is null)
        {
            // Stale / revoked / unknown cookie → treat as anonymous and
            // clear it so the browser stops sending the dead value.
            Response.Cookies.Delete(_sessions.CookieName);
            return AuthenticateResult.Fail("Session is no longer valid.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, validated.UserId.ToString()),
            new("sid", validated.SessionId.ToString()),
        };
        // Stamp the admin claim only when true — absence means "not admin"
        // so the RequireAdmin policy's RequireClaim is a clean allow/deny.
        if (validated.IsAdmin)
            claims.Add(new Claim(AuthPolicies.IsAdminClaim, AuthPolicies.IsAdminTrue));

        var identity = new ClaimsIdentity(claims, AuthSchemes.Cookie);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthSchemes.Cookie);
        return AuthenticateResult.Success(ticket);
    }
}
