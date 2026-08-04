using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

using Coffer.Api.Auth;
using Coffer.Api.Configuration;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Coffer.Api.Endpoints;

/// <summary>
/// OAuth 2.1 authorization + token endpoints (ADR-0063 §D2), the interactive
/// flow that mints the access tokens an MCP client uses. The authorization
/// endpoint reuses the existing WebAuthn cookie session: an anonymous request is
/// bounced to the SPA login, and a first-time client is bounced to the SPA
/// consent page; a returning (already-consented) client gets a code straight
/// away. The token endpoint is handled entirely by OpenIddict (PKCE-verified
/// code + refresh exchanges). Mapped only when MCP is enabled.
/// </summary>
public static class OAuthEndpoints
{
    /// <summary>Rate-limit policy name for anonymous DCR (<c>/oauth/register</c>),
    /// ADR-0081 D4. Configured in Program.cs as a per-IP fixed window.</summary>
    public const string DcrRateLimitPolicy = "mcp-dcr";

    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        // Authorization endpoint: GET decides (login / consent / issue); POST is
        // the consent decision posted back by the SPA consent page. Lambda form
        // so these bind as route handlers (IResult written), not RequestDelegate.
        routes.MapMethods("/oauth/authorize", new[] { "GET", "POST" },
            (HttpContext context,
             IOpenIddictApplicationManager applications,
             IOpenIddictAuthorizationManager authorizations,
             IOpenIddictScopeManager scopes)
                => AuthorizeAsync(context, applications, authorizations, scopes));
        // Cast through Delegate so the single-HttpContext handler is treated as a
        // route handler (its IResult is written) rather than a RequestDelegate.
        routes.MapPost("/oauth/token", (Delegate)(Func<HttpContext, Task<IResult>>)ExchangeAsync);
        // Dynamic Client Registration (RFC 7591). Anonymous, per the spec — a
        // client registers before it has any credentials. Capped + audited.
        routes.MapPost("/oauth/register", RegisterAsync)
              .AllowAnonymous()
              .RequireRateLimiting(DcrRateLimitPolicy);
        return routes;
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext context,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictScopeManager scopes)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

        // Reuse the WebAuthn cookie session. Anonymous → SPA login, returning
        // here afterwards. (prompt=login forces re-auth.)
        var auth = await context.AuthenticateAsync(AuthSchemes.Cookie).ConfigureAwait(false);
        if (!auth.Succeeded || request.HasPromptValue(PromptValues.Login))
        {
            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var userId = auth.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!;

        var application = await applications.FindByClientIdAsync(request.ClientId!).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The calling client application cannot be found.");
        var applicationId = await applications.GetIdAsync(application).ConfigureAwait(false);

        // Always grant offline_access so OpenIddict mints a refresh token. Without
        // it the access token just expires at SetAccessTokenLifetime (1h) and the
        // client must re-run the full interactive flow every hour — the refresh
        // flow is enabled but only triggers when offline_access is in the grant.
        // offline_access is permitted by the client's refresh-token grant
        // permission; it needs no separate scope permission. Matching on the full
        // granted set means a pre-fix consent (coffer.read only) is re-consented once
        // and re-created with offline_access, rather than silently reused.
        var requestedScopes = request.GetScopes();
        var grantedScopes = requestedScopes.Contains(Scopes.OfflineAccess)
            ? requestedScopes
            : requestedScopes.Add(Scopes.OfflineAccess);
        var existing = await authorizations.FindAsync(
                subject: userId,
                client: applicationId!,
                status: Statuses.Valid,
                type: AuthorizationTypes.Permanent,
                scopes: grantedScopes)
            .ToListAsync().ConfigureAwait(false);

        if (HttpMethods.IsPost(context.Request.Method))
        {
            // Consent decision posted by the SPA consent page. (Field is named
            // "decision", not "submit" — a form control named "submit" shadows
            // HTMLFormElement.submit() in the browser.)
            var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
            if (string.Equals(form["decision"], "deny", StringComparison.Ordinal))
            {
                return Results.Forbid(
                    authenticationSchemes: new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme },
                    properties: new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.AccessDenied,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                            "The user denied the authorization request.",
                    }));
            }
            // Allow → fall through to issue.
        }
        else if (existing.Count == 0)
        {
            // First-time client: GET with no prior consent → show the SPA consent
            // page, which posts the same parameters back here on Allow. Forward the
            // client's registered display name (display-only) so the page shows a
            // human label instead of the opaque client id.
            var displayName = await applications.GetDisplayNameAsync(application).ConfigureAwait(false)
                ?? request.ClientId!;
            return Results.Redirect(
                $"/oauth/consent{context.Request.QueryString}&client_name={Uri.EscapeDataString(displayName)}");
        }

        // Build the principal that becomes the authorization code (and, after
        // exchange, the access token whose subject drives RLS).
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters_AuthType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        identity.SetClaim(Claims.Subject, userId);

        identity.SetScopes(grantedScopes);
        identity.SetResources(await scopes.ListResourcesAsync(identity.GetScopes()).ToListAsync().ConfigureAwait(false));

        var authorization = existing.LastOrDefault()
            ?? await authorizations.CreateAsync(
                identity: identity,
                subject: userId,
                client: applicationId!,
                type: AuthorizationTypes.Permanent,
                scopes: identity.GetScopes()).ConfigureAwait(false);
        identity.SetAuthorizationId(await authorizations.GetIdAsync(authorization).ConfigureAwait(false));

        identity.SetDestinations(static _ => new[] { Destinations.AccessToken });

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            properties: null,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> ExchangeAsync(HttpContext context)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            throw new InvalidOperationException("The specified grant type is not supported.");

        // The principal stored with the code / refresh token. OpenIddict has
        // already validated PKCE, the code, and client authentication.
        var result = await context
            .AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
            .ConfigureAwait(false);
        var principal = result.Principal
            ?? throw new InvalidOperationException("The stored authorization principal cannot be retrieved.");

        // Re-assert destinations so the refreshed access token carries the subject
        // claim that drives RLS — the destinations stored with the refresh token
        // aren't guaranteed to survive the exchange. Mirrors AuthorizeAsync.
        if (principal.Identity is ClaimsIdentity identity)
            identity.SetDestinations(static _ => new[] { Destinations.AccessToken });

        return Results.SignIn(
            principal,
            properties: null,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // Dynamic Client Registration (RFC 7591). OpenIddict 7.5 has no built-in DCR
    // (slated for 7.6), so we implement the endpoint over the application manager.
    // This keeps the cap + redirect-URI validation under our direct control.
    private static async Task<IResult> RegisterAsync(
        ClientRegistrationRequest? request,
        IOpenIddictApplicationManager applications,
        IOptions<ApiOptions> options,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Coffer.Mcp.Dcr");

        if (request?.RedirectUris is not { Length: > 0 })
            return RegistrationError("invalid_client_metadata", "redirect_uris is required.");

        var redirectUris = new List<Uri>();
        foreach (var raw in request.RedirectUris)
        {
            // Only absolute https (or loopback for native clients) — never an
            // attacker-supplied http origin to redirect codes to.
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                || !(uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback))
                return RegistrationError("invalid_redirect_uri",
                    $"'{raw}' must be an absolute https (or loopback) URI.");
            redirectUris.Add(uri);
        }

        var cap = options.Value.Mcp.MaxDynamicClients;
        var count = await applications.CountAsync().ConfigureAwait(false);
        if (count >= cap)
        {
            logger.LogWarning("MCP DCR rejected: client cap {Cap} reached ({Count}).", cap, count);
            return Results.Json(
                new { error = "access_denied", error_description = "Client registration limit reached." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var authMethod = string.IsNullOrWhiteSpace(request.TokenEndpointAuthMethod)
            ? "none" : request.TokenEndpointAuthMethod!;
        var isPublic = string.Equals(authMethod, "none", StringComparison.OrdinalIgnoreCase);
        var clientId = Guid.NewGuid().ToString("N");
        var clientSecret = isPublic ? null : GenerateClientSecret();
        var displayName = string.IsNullOrWhiteSpace(request.ClientName) ? clientId : request.ClientName!;

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientType = isPublic ? ClientTypes.Public : ClientTypes.Confidential,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = displayName,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + "coffer.read",
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        foreach (var uri in redirectUris)
            descriptor.RedirectUris.Add(uri);
        await applications.CreateAsync(descriptor).ConfigureAwait(false);

        logger.LogInformation(
            "MCP DCR: registered {Type} client {ClientId} '{ClientName}' with {RedirectCount} redirect URI(s).",
            isPublic ? "public" : "confidential", clientId, displayName, redirectUris.Count);

        var response = new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["client_name"] = displayName,
            ["redirect_uris"] = request.RedirectUris,
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = authMethod,
            ["scope"] = "coffer.read offline_access",
        };
        if (clientSecret is not null)
        {
            response["client_secret"] = clientSecret;
            response["client_secret_expires_at"] = 0; // does not expire
        }
        return Results.Json(response, statusCode: StatusCodes.Status201Created);
    }

    private static IResult RegistrationError(string error, string description) =>
        Results.Json(new { error, error_description = description },
            statusCode: StatusCodes.Status400BadRequest);

    private static string GenerateClientSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>RFC 7591 client-registration request (the subset we honor).</summary>
    private sealed record ClientRegistrationRequest
    {
        [JsonPropertyName("client_name")] public string? ClientName { get; init; }
        [JsonPropertyName("redirect_uris")] public string[]? RedirectUris { get; init; }
        [JsonPropertyName("token_endpoint_auth_method")] public string? TokenEndpointAuthMethod { get; init; }
        [JsonPropertyName("grant_types")] public string[]? GrantTypes { get; init; }
        [JsonPropertyName("response_types")] public string[]? ResponseTypes { get; init; }
        [JsonPropertyName("scope")] public string? Scope { get; init; }
    }

    // OpenIddict only requires the identity to be authenticated (any non-empty
    // authentication type); the constant keeps it readable.
    private const string TokenValidationParameters_AuthType = "Coffer.OAuth";
}
