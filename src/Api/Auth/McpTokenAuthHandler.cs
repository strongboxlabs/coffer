using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Auth;

/// <summary>
/// AuthenticationHandler for the MCP bearer scheme (ADR-0063). Reads
/// <c>Authorization: Bearer &lt;token&gt;</c>, hashes the presented value, and
/// validates it against <c>mcp_access_tokens</c>. On success it surfaces the
/// owning user via <see cref="HttpContext.User"/> with the same
/// <c>NameIdentifier</c> claim the cookie handler stamps, so the RLS connection
/// interceptor sets <c>app.user_id</c> and the MCP tools run as that user.
/// </summary>
/// <remarks>
/// This scheme authenticates ONLY the <c>/mcp</c> endpoint (the
/// <see cref="AuthPolicies.RequireMcp"/> policy lists just this scheme). It is
/// deliberately absent from the default policy, so a read-only MCP token can
/// never authenticate a REST mutation endpoint. The token plaintext never
/// touches the DB — only its SHA-256 — so a DB read can't forge one.
/// </remarks>
public sealed class McpTokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string BearerPrefix = "Bearer ";

    private readonly McpTokensRepository _tokens;

    public McpTokenAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        McpTokensRepository tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // No bearer token: contribute no identity (NoResult, not Fail) so the
            // 401 challenge is clean and other schemes — if ever added to this
            // endpoint's policy — still get a turn.
            return AuthenticateResult.NoResult();
        }

        var presented = header[BearerPrefix.Length..].Trim();
        if (presented.Length == 0)
            return AuthenticateResult.NoResult();

        var validated = await _tokens.ValidateAsync(
            McpTokenService.Hash(presented), Context.RequestAborted).ConfigureAwait(false);
        if (validated is null)
            return AuthenticateResult.Fail("MCP token is invalid, expired, or revoked.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, validated.UserId.ToString()),
            new("scope", validated.Scopes),
        };
        if (validated.IsAdmin)
            claims.Add(new Claim(AuthPolicies.IsAdminClaim, AuthPolicies.IsAdminTrue));

        var identity = new ClaimsIdentity(claims, AuthSchemes.Mcp);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthSchemes.Mcp);
        return AuthenticateResult.Success(ticket);
    }
}
