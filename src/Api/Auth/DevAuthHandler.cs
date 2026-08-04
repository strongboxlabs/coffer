using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Coffer.Api.Auth;

/// <summary>
/// Development-only authentication handler. Treats every request as the
/// bootstrap "system" user (<c>00000000-0000-0000-0000-000000000001</c>) so
/// integration tests and local-dev scenarios can hit authenticated endpoints
/// without driving a real WebAuthn ceremony.
/// </summary>
/// <remarks>
/// Registration is gated in <c>Program.cs</c> by both
/// <c>ASPNETCORE_ENVIRONMENT=Development</c> AND <c>Api:DevAuth=true</c>
/// per ADR-0013. Both gates must hold; a production build with
/// <c>DevAuth=true</c> still rejects requests because the handler is never
/// registered. Tests trigger the gate by setting the matching env vars
/// before <c>WebApplication.CreateBuilder(args)</c> runs (see
/// <c>ApiFactory.ApplyEnvOverrides</c>) — process-global mutation that's
/// safe inside the sequential <c>ApiCollection</c>.
/// </remarks>
public sealed class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// Bootstrap "system" user id seeded by migration 014.
    /// </summary>
    public static readonly Guid SystemUserId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Per-request opt-in header. DevAuth only authenticates when the caller
    /// sends <c>X-Dev-Auth: 1</c>; without it the handler falls through so the
    /// Cookie scheme's result stands. The browser SPA never sends this header,
    /// so a real cookie session is never hijacked (the bug this guards against);
    /// integration tests + explicit local-dev callers set it to bypass WebAuthn.
    /// </summary>
    public const string OptInHeader = "X-Dev-Auth";

    public DevAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Opt-in per request. Without the explicit header, fall through
        // (NoResult) so this scheme contributes no identity and the Cookie
        // scheme's result is authoritative — even when DevAuth is registered
        // and listed in the default policy. Fixes the prior hijack where a
        // cookie-bearing request still resolved to the system user.
        if (!Request.Headers.TryGetValue(OptInHeader, out var optIn)
            || optIn != "1")
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, SystemUserId.ToString()),
                new Claim(ClaimTypes.Name, "system (dev-auth)"),
                // Dev-auth is the operator's local short-circuit; treat it as
                // admin so RequireAdmin endpoints are reachable in dev + tests
                // without minting a real admin cookie. Dev-only: this handler
                // is never registered outside Development (ADR-0013 dual gate).
                new Claim(AuthPolicies.IsAdminClaim, AuthPolicies.IsAdminTrue),
            },
            AuthSchemes.DevAuth);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthSchemes.DevAuth);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
