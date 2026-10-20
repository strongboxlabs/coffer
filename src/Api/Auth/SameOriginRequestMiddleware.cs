using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Configuration;

namespace Coffer.Api.Auth;

/// <summary>
/// Refuses a state-changing request that carries the session cookie unless the browser
/// says it came from Coffer's own origin (2026-10-12 audit, M1).
/// </summary>
/// <remarks>
/// <para>Cross-site request forgery protection used to be the cookie's SameSite=Strict
/// alone. That stops OTHER SITES, but a sibling subdomain of the same domain is the same
/// site, and on a homelab domain that is often another app. A page there could post a
/// form to <c>/oauth/authorize</c> and get an MCP token for whoever was signed in, or
/// stage a restore of its own archive against an admin. JSON endpoints were protected
/// only incidentally (a form can't send JSON); multipart and body-less posts weren't.
/// The <c>.DisableAntiforgery()</c> calls in the endpoints never did anything: no
/// antiforgery service was registered for them to disable.</para>
///
/// <para>This checks every unsafe method in one place. Modern browsers send
/// <c>Sec-Fetch-Site</c>; only <c>same-origin</c> passes. A browser that doesn't send
/// it sends <c>Origin</c> on a cross-origin post, which must be this request's own
/// origin or one of the configured browser origins. A request with neither is not from
/// a browser — a script or CLI holding a cookie it was given — and CSRF does not apply
/// to it.</para>
///
/// <para>Requests without the session cookie are untouched: anonymous endpoints, MCP and
/// OAuth clients (bearer tokens), the token endpoint. Only the cookie makes a request
/// forgeable.</para>
/// </remarks>
public sealed class SameOriginRequestMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _configuredOrigins;

    public SameOriginRequestMiddleware(RequestDelegate next, IOptions<ApiOptions> options)
    {
        _next = next;
        _configuredOrigins = new HashSet<string>(
            options.Value.Fido2.Origins.Select(o => o.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsUnsafe(context.Request.Method)
            && CarriesSessionCookie(context)
            && !IsSameOrigin(context.Request))
        {
            await Results.Problem(
                    "This request came from another site and was refused. If you clicked a link "
                    + "or button elsewhere, open Coffer directly and try again.",
                    statusCode: StatusCodes.Status403Forbidden)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsUnsafe(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
          || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

    private static bool CarriesSessionCookie(HttpContext context)
    {
        var name = context.RequestServices.GetRequiredService<SessionService>().CookieName;
        return context.Request.Cookies.ContainsKey(name);
    }

    internal bool IsSameOrigin(HttpRequest request)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0)
            return string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase);

        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0)
            return string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
                || _configuredOrigins.Contains(origin.TrimEnd('/'));

        return true;
    }
}
