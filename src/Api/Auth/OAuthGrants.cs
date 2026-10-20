using OpenIddict.Abstractions;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Coffer.Api.Auth;

/// <summary>
/// How long a connection made through OAuth lasts, and what a user may do with theirs
/// (FU-087).
/// </summary>
/// <remarks>
/// Refresh tokens are sliding: each refresh issues the next, so an app that kept
/// refreshing kept its access for ever, and nothing listed it for the user to end. A grant
/// now lasts <see cref="MaxAge"/> from the consent that created it, however often the app
/// refreshes. After that the token endpoint refuses it, and the next connection is a new
/// consent. Users see and revoke their grants on Account → Security, beside their MCP
/// tokens.
/// </remarks>
public static class OAuthGrants
{
    /// <summary>A grant's longest life, counted from its authorization (decided 2026-10-19).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    /// <summary>
    /// How recent a sign-in satisfies <c>prompt=login</c>. The login page sends the user
    /// back to the very authorization request that carried it, so honouring the prompt on
    /// every visit sent a freshly signed-in user straight back to the login page.
    /// </summary>
    public static readonly TimeSpan PromptLoginFreshness = TimeSpan.FromMinutes(5);

    /// <summary>Whether a grant created at <paramref name="createdAt"/> is past its age.
    /// One with no recorded creation is treated as past it: nothing vouches for its age.</summary>
    public static bool IsExpired(DateTimeOffset? createdAt, DateTimeOffset now) =>
        createdAt is not { } at || now - at > MaxAge;

    /// <summary>
    /// End a grant and every token issued under it. Used when a user revokes it, and when
    /// one is found past <see cref="MaxAge"/>.
    /// </summary>
    public static async Task RevokeAsync(
        IOpenIddictAuthorizationManager authorizations, IOpenIddictTokenManager tokens,
        object authorization, CancellationToken ct)
    {
        var id = await authorizations.GetIdAsync(authorization, ct).ConfigureAwait(false);
        await authorizations.TryRevokeAsync(authorization, ct).ConfigureAwait(false);
        if (id is not null)
            await tokens.RevokeByAuthorizationIdAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Give every registered client the revocation endpoint (RFC 7009). Clients registered
    /// before it existed carry only the authorization and token endpoint permissions, and
    /// OpenIddict refuses an endpoint a client holds no permission for. Idempotent.
    /// </summary>
    /// <returns>How many clients were changed.</returns>
    public static async Task<int> GrantRevocationToEveryClientAsync(
        IOpenIddictApplicationManager applications, CancellationToken ct)
    {
        // Read them all first: updating one while the listing still streams is a second
        // command on the connection the listing holds open.
        var all = new List<object>();
        await foreach (var application in applications.ListAsync(count: null, offset: null, ct).ConfigureAwait(false))
            all.Add(application);

        var changed = 0;
        foreach (var application in all)
        {
            if (await applications.HasPermissionAsync(application, Permissions.Endpoints.Revocation, ct).ConfigureAwait(false))
                continue;
            var descriptor = new OpenIddictApplicationDescriptor();
            await applications.PopulateAsync(descriptor, application, ct).ConfigureAwait(false);
            descriptor.Permissions.Add(Permissions.Endpoints.Revocation);
            await applications.UpdateAsync(application, descriptor, ct).ConfigureAwait(false);
            changed++;
        }
        return changed;
    }
}
