namespace Coffer.Api.Auth;

/// <summary>
/// The browser security headers, on every response (FU-091).
/// </summary>
/// <remarks>
/// <para>The app sent none, and the docs did not ask the proxy for them, so whether an install
/// had them depended on a proxy configuration nobody was told to write. Set here, every
/// install has them whatever is in front of it; a proxy that adds its own is harmless.</para>
/// <list type="bullet">
/// <item><b>Content-Security-Policy.</b> Everything from this origin and nothing else: the
/// fonts are bundled, the pre-paint theme script is a file rather than inline, and the SPA
/// talks only to its own API. <c>frame-ancestors 'none'</c> stops the app being framed.
/// No <c>form-action</c>: the OAuth consent is a form posted here that redirects to the
/// connector's own callback, and browsers apply <c>form-action</c> to that redirect.</item>
/// <item><b>X-Content-Type-Options: nosniff</b>, so an upload served back is never run as
/// something it was not labelled as.</item>
/// <item><b>Referrer-Policy: same-origin</b>: a page's address, which can carry ids, is
/// sent to no one else.</item>
/// <item><b>Strict-Transport-Security</b>, on HTTPS responses only (the scheme the trusted
/// proxy forwarded), for a year, without <c>includeSubDomains</c>: other hosts under the
/// same domain are not this app's to decide for.</item>
/// <item><b>X-Frame-Options: DENY</b> and <b>Permissions-Policy</b> for older browsers and
/// for features the app never uses.</item>
/// </list>
/// <para>Not set: Cross-Origin-Opener-Policy. A connector that runs its OAuth sign-in in a
/// popup reads the result through <c>window.opener</c>, which a same-origin opener policy
/// on the consent page would cut.</para>
/// </remarks>
public sealed class SecurityHeadersMiddleware
{
    internal const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; "
        + "font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; "
        + "frame-ancestors 'none'";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            if (context.Request.IsHttps)
                headers.StrictTransportSecurity = "max-age=31536000";
            return Task.CompletedTask;
        });
        return _next(context);
    }
}
