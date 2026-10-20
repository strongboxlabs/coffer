using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenIddict.Abstractions;

using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Auth;

/// <summary>
/// Ends everything a user holds right now: browser sessions, MCP bearer tokens, and
/// OAuth grants with the tokens issued under them.
/// </summary>
/// <remarks>
/// <para>Disabling a user used to block only NEW logins (2026-10-12 audit, H2). An
/// open session kept working for up to 30 days, an MCP token for 90 or forever, an
/// OAuth grant for as long as its client kept refreshing — and a disabled admin could
/// re-enable themselves. Disabling now calls <see cref="RevokeEverywhereAsync"/>, and
/// re-enabling does not bring any of it back: the user signs in again and reconnects
/// their apps. Every credential check also refuses a disabled user, which covers a
/// credential minted in the same instant.</para>
///
/// <para>The OAuth managers exist only when MCP is enabled (they are registered with
/// the authorization server). With MCP off there are no grants to revoke, so they are
/// resolved optionally rather than required.</para>
/// </remarks>
public sealed class UserAccessRevoker
{
    private readonly SessionsRepository _sessions;
    private readonly McpTokensRepository _mcpTokens;
    private readonly IServiceProvider _services;
    private readonly ILogger<UserAccessRevoker> _logger;

    public UserAccessRevoker(
        SessionsRepository sessions,
        McpTokensRepository mcpTokens,
        IServiceProvider services,
        ILogger<UserAccessRevoker> logger)
    {
        _sessions = sessions;
        _mcpTokens = mcpTokens;
        _services = services;
        _logger = logger;
    }

    /// <summary>What was revoked, for the log and the caller's response.</summary>
    public sealed record Revoked(int Sessions, int McpTokens, long OAuthAuthorizations, long OAuthTokens);

    /// <summary>Revoke every session, MCP token and OAuth grant <paramref name="userId"/> holds.</summary>
    public async Task<Revoked> RevokeEverywhereAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var sessions = await _sessions.RevokeAllForUserAsync(userId, cancellationToken).ConfigureAwait(false);
        var mcpTokens = await _mcpTokens.RevokeAllForUserAsync(userId, cancellationToken).ConfigureAwait(false);

        long authorizations = 0, tokens = 0;
        var subject = userId.ToString();
        var authorizationManager = _services.GetService<IOpenIddictAuthorizationManager>();
        var tokenManager = _services.GetService<IOpenIddictTokenManager>();
        // Authorizations first: a token whose authorization is revoked is refused even
        // if revoking the token itself were to fail.
        if (authorizationManager is not null)
            authorizations = await authorizationManager.RevokeBySubjectAsync(subject, cancellationToken)
                                                       .ConfigureAwait(false);
        if (tokenManager is not null)
            tokens = await tokenManager.RevokeBySubjectAsync(subject, cancellationToken)
                                       .ConfigureAwait(false);

        var revoked = new Revoked(sessions, mcpTokens, authorizations, tokens);
        _logger.LogInformation(
            "Revoked access for user {UserId}: {Sessions} session(s), {McpTokens} MCP token(s), "
            + "{Authorizations} OAuth grant(s), {Tokens} OAuth token(s).",
            userId, revoked.Sessions, revoked.McpTokens, revoked.OAuthAuthorizations, revoked.OAuthTokens);
        return revoked;
    }
}
