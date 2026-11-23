using OpenIddict.Abstractions;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Coffer.Api.Auth;

/// <summary>
/// How long a connection made through OAuth lasts, and what a user may do with theirs
/// (FU-087).
/// </summary>
/// <remarks>
/// Refresh tokens are sliding: each refresh issues the next, so on their own an app that
/// keeps refreshing keeps its access for ever. A grant therefore lasts
/// <see cref="MaxAge"/> from the consent that created it, however often the app
/// refreshes. After that the token endpoint refuses it, and the next connection is a new
/// consent. Users see and revoke their grants on Account → Security, beside their MCP
/// tokens.
/// </remarks>
public static class OAuthGrants
{
    /// <summary>A grant's longest life, counted from its authorization.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    /// <summary>
    /// How recent a sign-in satisfies <c>prompt=login</c>. The login page sends the user
    /// back to the very authorization request that carried it, so honouring the prompt on
    /// every visit would send a freshly signed-in user straight back to the login page.
    /// </summary>
    public static readonly TimeSpan PromptLoginFreshness = TimeSpan.FromMinutes(5);

    /// <summary>Whether a grant created at <paramref name="createdAt"/> is past its age.
    /// One with no recorded creation is treated as past it: nothing vouches for its age.</summary>
    public static bool IsExpired(DateTimeOffset? createdAt, DateTimeOffset now) =>
        createdAt is not { } at || now - at > MaxAge;

    /// <summary>
    /// Whether an authorization is a live connection: valid and inside <see cref="MaxAge"/>.
    /// A revoked or past-its-age one can no longer do anything. The one rule for what the
    /// connection list shows, what a client's connection count counts, and what keeps a
    /// client from being pruned, so the three cannot disagree.
    /// </summary>
    public static async Task<bool> IsLiveAsync(
        IOpenIddictAuthorizationManager authorizations, object authorization, DateTimeOffset now,
        CancellationToken ct)
    {
        if (!await authorizations.HasStatusAsync(authorization, Statuses.Valid, ct).ConfigureAwait(false))
            return false;
        var created = await authorizations.GetCreationDateAsync(authorization, ct).ConfigureAwait(false);
        return !IsExpired(created, now);
    }

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
