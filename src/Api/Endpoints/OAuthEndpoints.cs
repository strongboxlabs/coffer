using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

using Coffer.Api.Auth;
using Coffer.Api.Configuration;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Coffer.Api.Endpoints;

/// <summary>
/// OAuth 2.1 authorization + token endpoints (ADR-0063 §D2), the interactive
/// flow that mints the access tokens an MCP client uses. The authorization
/// endpoint reuses the existing WebAuthn cookie session: an anonymous request is
/// bounced to the SPA login, and a first-time client is bounced to the SPA
/// consent page; a returning (already-consented) client gets a code straight
/// away. OpenIddict validates the token endpoint's PKCE-verified code and refresh
/// exchanges, then passes them here to check the user and the grant's age before
/// it signs. Revocation (RFC 7009) is OpenIddict's own. Mapped only when MCP is
/// enabled.
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
                => AuthorizeAsync(context, applications, authorizations, scopes))
              // Anonymous on purpose: an anonymous caller is redirected to the login page
              // by the handler, which a 401 from the fallback policy would not do.
              .AllowAnonymous();
        // Cast through Delegate so the single-HttpContext handler is treated as a
        // route handler (its IResult is written) rather than a RequestDelegate.
        routes.MapPost("/oauth/token", (Delegate)(Func<HttpContext, Task<IResult>>)ExchangeAsync)
              // The client authenticates with its code or refresh token, which OpenIddict
              // has validated before this runs.
              .AllowAnonymous();
        // Dynamic Client Registration (RFC 7591). Anonymous, per the spec — a
        // client registers before it has any credentials. Capped + audited.
        routes.MapPost("/oauth/register", RegisterAsync)
              .AllowAnonymous()
              .RequireRateLimiting(DcrRateLimitPolicy);
        // What the consent page shows, fetched by its ticket so it is what the server
        // registered rather than what the link says (2026-10-12 audit, H1).
        routes.MapGet("/api/oauth/consent/{ticket}", ConsentInfoAsync)
              .RequireAuthorization();
        return routes;
    }

    /// <summary>
    /// The only redirect URIs a client may register or use: https, or plain http to a
    /// loopback host for a native client listening on localhost.
    /// </summary>
    /// <remarks>
    /// The check used to be <c>scheme == https || uri.IsLoopback</c>. <c>IsLoopback</c>
    /// looks only at the host, so <c>javascript://localhost/…</c> passed — and with
    /// form_post, approving consent ran that script on Coffer's origin (2026-10-12 audit,
    /// H1, confirmed by test). The scheme is now checked on both sides.
    /// </remarks>
    /// <summary>An OAuth error answered by OpenIddict, which returns it to the client in
    /// the form the endpoint calls for: a redirect from authorize, JSON from token.</summary>
    private static IResult OAuthError(string error, string description) =>
        Results.Forbid(
            authenticationSchemes: new[] { OpenIddictServerAspNetCoreDefaults.AuthenticationScheme },
            properties: new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }));

    internal static bool IsAllowedRedirect(Uri uri) =>
        uri.IsAbsoluteUri
        && (uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    private static async Task<IResult> AuthorizeAsync(
        HttpContext context,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictScopeManager scopes)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");

        var clock = context.RequestServices.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();

        // Reuse the WebAuthn cookie session. Anonymous → SPA login, returning here
        // afterwards.
        var auth = await context.AuthenticateAsync(AuthSchemes.Cookie).ConfigureAwait(false);

        // prompt=none asks for an answer without any page (FU-095): no session is
        // login_required, sent back to the client, not a redirect to the login page.
        if (!auth.Succeeded && request.HasPromptValue(PromptValues.None))
            return OAuthError(OpenIddictConstants.Errors.LoginRequired, "The user is not signed in.");

        // prompt=login asks for a fresh sign-in. The login page returns to this same
        // request, prompt and all, so a session begun moments ago is the sign-in it asked
        // for; honouring the prompt on every visit sent the user straight back to the
        // login page, for ever (FU-095).
        var freshSignIn = auth.Succeeded
            && SessionClaims.CreatedAtOf(auth.Principal!) is { } began
            && now.UtcDateTime - began <= OAuthGrants.PromptLoginFreshness;
        if (!auth.Succeeded || (request.HasPromptValue(PromptValues.Login) && !freshSignIn))
        {
            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            return Results.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var userId = auth.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!;

        var application = await applications.FindByClientIdAsync(request.ClientId!).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The calling client application cannot be found.");
        var applicationId = await applications.GetIdAsync(application).ConfigureAwait(false);

        // OpenIddict has matched the redirect_uri to the client's registration; this
        // refuses a registration made before the scheme rule existed. Nothing is
        // redirected to an address we would not accept today.
        if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirect)
            || !IsAllowedRedirect(redirect))
            return Results.Problem(
                "This app's redirect address is not allowed. Remove the app in System → MCP and "
                + "connect it again.",
                statusCode: StatusCodes.Status400BadRequest);

        var tickets = context.RequestServices.GetRequiredService<OAuthConsentTickets>();

        // Always grant offline_access so OpenIddict mints a refresh token. Without
        // it the access token just expires at SetAccessTokenLifetime (1h) and the
        // client must re-run the full interactive flow every hour — the refresh
        // flow is enabled but only triggers when offline_access is in the grant.
        // offline_access is permitted by the client's refresh-token grant
        // permission; it needs no separate scope permission. Matching on the full
        // granted set means a pre-fix consent (coffer.read only) is re-consented once
        // and re-created with offline_access, rather than silently reused.
        //
        // coffer.read likewise (FU-095): /mcp requires it, and writing implies reading, so
        // a client that asked for coffer.write alone is granted the read it needs.
        var grantedScopes = request.GetScopes()
            .Union([Scopes.OfflineAccess, McpScopes.Read], StringComparer.Ordinal)
            .ToImmutableArray();
        var matching = await authorizations.FindAsync(
                subject: userId,
                client: applicationId!,
                status: Statuses.Valid,
                type: AuthorizationTypes.Permanent,
                scopes: grantedScopes)
            .ToListAsync().ConfigureAwait(false);

        // A consent lasts OAuthGrants.MaxAge (FU-087). One past it is revoked here rather
        // than reused, so a returning client is asked again and gets a new grant, instead
        // of a code under a grant the token endpoint would refuse.
        var tokens = context.RequestServices.GetRequiredService<IOpenIddictTokenManager>();
        var existing = new List<object>();
        foreach (var candidate in matching)
        {
            if (OAuthGrants.IsExpired(await authorizations.GetCreationDateAsync(candidate).ConfigureAwait(false), now))
                await OAuthGrants.RevokeAsync(authorizations, tokens, candidate, context.RequestAborted).ConfigureAwait(false);
            else
                existing.Add(candidate);
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            // Consent decision posted by the SPA consent page. (Field is named
            // "decision", not "submit" — a form control named "submit" shadows
            // HTMLFormElement.submit() in the browser.)
            var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
            var decision = form["decision"].ToString();
            if (string.Equals(decision, "deny", StringComparison.Ordinal))
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

            // Allow must be said, not assumed: a missing decision used to mean allow.
            // And it must carry the ticket minted when this user was shown consent for
            // this exact request (OAuthConsentTickets) — a page that merely posts here,
            // from a sibling subdomain say, never saw one.
            if (!string.Equals(decision, "allow", StringComparison.Ordinal)
                || !tickets.TryConsume(
                    form["consent_ticket"].ToString(), Guid.Parse(userId), request.ClientId!,
                    request.RedirectUri!, request.GetScopes(), request.CodeChallenge ?? string.Empty))
                return Results.Problem(
                    "This approval has expired or was already used. Start connecting the app again.",
                    statusCode: StatusCodes.Status400BadRequest);
            // Allow → fall through to issue.
        }
        else if (existing.Count == 0 && request.HasPromptValue(PromptValues.None))
        {
            // Consent would need a page, and prompt=none forbids one (FU-095).
            return OAuthError(OpenIddictConstants.Errors.ConsentRequired, "The user has not approved this app.");
        }
        else if (existing.Count == 0)
        {
            // First-time client: GET with no prior consent → show the SPA consent
            // page, which posts the same parameters back here on Allow together with
            // the ticket. The page fetches the client's REGISTERED name and redirect by
            // that ticket; nothing it shows comes from the link.
            var ticket = tickets.Mint(
                Guid.Parse(userId), request.ClientId!, request.RedirectUri!, request.GetScopes(),
                request.CodeChallenge ?? string.Empty);
            // Drop any consent_ticket / client_name the link itself carried, so the only
            // one the page sees is ours.
            var forwarded = QueryString.Create(context.Request.Query
                    .Where(kv => kv.Key is not ("consent_ticket" or "client_name")))
                .Add("consent_ticket", ticket);
            return Results.Redirect($"/oauth/consent{forwarded}");
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

        // A refresh re-signed the stored principal with no look at the user, so a
        // disabled user's app kept access for as long as it kept refreshing
        // (2026-10-12 audit, H2). Disabling revokes their grants; this also covers a
        // code or refresh token minted in the same instant.
        var users = context.RequestServices.GetRequiredService<UsersRepository>();
        if (!Guid.TryParse(principal.GetClaim(Claims.Subject), out var subject)
            || !await users.IsActiveAsync(subject, context.RequestAborted).ConfigureAwait(false))
        {
            return OAuthError(OpenIddictConstants.Errors.InvalidGrant, "The account this app was connected with is no longer active.");
        }

        // A grant lasts OAuthGrants.MaxAge from its consent, however often it refreshes
        // (FU-087). Refresh tokens slide, so before this an app that kept refreshing kept
        // its access for ever. Past it, the grant and its tokens are revoked, and the app
        // has to be connected again.
        var authorizations = context.RequestServices.GetRequiredService<IOpenIddictAuthorizationManager>();
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var authorizationId = principal.GetAuthorizationId();
        var authorization = authorizationId is null
            ? null
            : await authorizations.FindByIdAsync(authorizationId, context.RequestAborted).ConfigureAwait(false);
        if (authorization is null
            || OAuthGrants.IsExpired(await authorizations.GetCreationDateAsync(authorization, context.RequestAborted).ConfigureAwait(false), now))
        {
            if (authorization is not null)
                await OAuthGrants.RevokeAsync(authorizations,
                    context.RequestServices.GetRequiredService<IOpenIddictTokenManager>(),
                    authorization, context.RequestAborted).ConfigureAwait(false);
            return OAuthError(OpenIddictConstants.Errors.InvalidGrant,
                $"This app's connection is more than {OAuthGrants.MaxAge.Days} days old. Connect it again.");
        }

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
        ILoggerFactory loggerFactory,
        TimeProvider clock)
    {
        var logger = loggerFactory.CreateLogger("Coffer.Mcp.Dcr");

        if (request?.RedirectUris is not { Length: > 0 })
            return RegistrationError("invalid_client_metadata", "redirect_uris is required.");

        var redirectUris = new List<Uri>();
        foreach (var raw in request.RedirectUris)
        {
            // Only absolute https, or http to localhost for native clients — never an
            // attacker-supplied http origin to redirect codes to, nor any other scheme.
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !IsAllowedRedirect(uri))
                return RegistrationError("invalid_redirect_uri",
                    $"'{raw}' must be an absolute https URI, or http on localhost.");
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
                // RFC 7009 (FU-087): a client may end its own tokens.
                Permissions.Endpoints.Revocation,
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
            ["client_id_issued_at"] = clock.GetUtcNow().ToUnixTimeSeconds(),
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

    private static async Task<IResult> ConsentInfoAsync(
        string ticket,
        ICurrentUserAccessor currentUser,
        OAuthConsentTickets tickets,
        IOpenIddictApplicationManager applications,
        CancellationToken cancellationToken)
    {
        if (tickets.Peek(ticket, currentUser.UserId) is not { } pending)
            return Results.NotFound();

        var application = await applications.FindByClientIdAsync(pending.ClientId, cancellationToken)
                                            .ConfigureAwait(false);
        if (application is null)
            return Results.NotFound();
        var name = await applications.GetDisplayNameAsync(application, cancellationToken).ConfigureAwait(false);
        var host = Uri.TryCreate(pending.RedirectUri, UriKind.Absolute, out var uri)
            ? uri.Authority
            : pending.RedirectUri;
        return Results.Ok(new OAuthConsentInfo(
            ClientId: pending.ClientId,
            ClientName: string.IsNullOrWhiteSpace(name) ? null : name,
            RedirectUri: pending.RedirectUri,
            RedirectHost: host,
            Scopes: pending.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
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

    /// <summary>
    /// What the consent page shows: the client and redirect as REGISTERED. The name is
    /// still whatever the app chose when it registered — the page says so — but no
    /// longer whatever a link says.
    /// </summary>
    public sealed record OAuthConsentInfo(
        string ClientId, string? ClientName, string RedirectUri, string RedirectHost,
        IReadOnlyList<string> Scopes);

    // OpenIddict only requires the identity to be authenticated (any non-empty
    // authentication type); the constant keeps it readable.
    private const string TokenValidationParameters_AuthType = "Coffer.OAuth";
}
