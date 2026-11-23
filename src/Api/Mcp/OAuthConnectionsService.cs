using OpenIddict.Abstractions;

using Coffer.Api.Auth;
using Coffer.Api.Db.Repositories;

using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Coffer.Api.Mcp;

/// <summary>
/// One OAuth connection to <c>/mcp</c>: an app a person signed in to, with the name they gave
/// it and when it was last used.
/// </summary>
/// <param name="Id">The OpenIddict authorization id.</param>
/// <param name="AppName">What the client registered itself as. Every install of one client
/// reports the same string, which is why <paramref name="Label"/> exists.</param>
/// <param name="Label">The owner's name for this connection, or null.</param>
/// <param name="LastUsedAt">Null when no use has been recorded: a connection that predates the
/// record, or one never used since.</param>
/// <param name="ClientId">The OAuth client id of the registration this connection was made
/// through, which is what groups it under its app; null when the registration is gone.</param>
public sealed record OAuthConnection(
    string Id,
    Guid UserId,
    string? ClientId,
    string AppName,
    string? Label,
    bool CanWrite,
    DateTimeOffset ConnectedAt,
    DateTimeOffset ExpiresAt,
    DateTime? LastUsedAt);

/// <summary>
/// The OAuth connections people hold, for the user's own list (Account → Security) and the
/// admin's list across users (System → MCP). One reader for both, so what a user sees of their
/// own connection and what an admin sees of it cannot drift apart.
/// </summary>
/// <remarks>
/// OpenIddict's tables have no row-level security (its stores run where no user is signed in),
/// so ownership is checked here, on every read and every write: a caller that passes an owner
/// is matched against the authorization's subject, and someone else's authorization answers
/// exactly as a missing one does. A null owner is the admin path, reached only from
/// <c>RequireAdmin</c> endpoints.
/// </remarks>
public sealed class OAuthConnectionsService
{
    private readonly IOpenIddictAuthorizationManager _authorizations;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOpenIddictTokenManager _tokens;
    private readonly OAuthConnectionMetaRepository _meta;
    private readonly TimeProvider _clock;

    public OAuthConnectionsService(
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictApplicationManager applications,
        IOpenIddictTokenManager tokens,
        OAuthConnectionMetaRepository meta,
        TimeProvider clock)
    {
        _authorizations = authorizations;
        _applications = applications;
        _tokens = tokens;
        _meta = meta;
        _clock = clock;
    }

    /// <summary>
    /// The live connections of <paramref name="owner"/>, or of everyone when null. Live means
    /// valid and inside its <see cref="OAuthGrants.MaxAge"/>: a past-its-age connection can no
    /// longer do anything, so it is not one to show. Newest first.
    /// </summary>
    public async Task<IReadOnlyList<OAuthConnection>> ListAsync(Guid? owner, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        // Read every authorization before asking about any: Npgsql has no MARS, so a query
        // issued while the listing's reader is still open throws.
        var all = new List<object>();
        var source = owner is { } user
            ? _authorizations.FindBySubjectAsync(user.ToString(), cancellationToken)
            : _authorizations.ListAsync(count: null, offset: null, cancellationToken);
        await foreach (var authorization in source.ConfigureAwait(false))
            all.Add(authorization);

        var live = new List<(string Id, Guid User, string? ApplicationId, bool CanWrite, DateTimeOffset Created)>();
        foreach (var authorization in all)
        {
            if (!await OAuthGrants.IsLiveAsync(_authorizations, authorization, now, cancellationToken).ConfigureAwait(false))
                continue;
            var created = await _authorizations.GetCreationDateAsync(authorization, cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(
                    await _authorizations.GetSubjectAsync(authorization, cancellationToken).ConfigureAwait(false),
                    out var subject))
                continue;

            var scopes = await _authorizations.GetScopesAsync(authorization, cancellationToken).ConfigureAwait(false);
            live.Add((
                (await _authorizations.GetIdAsync(authorization, cancellationToken).ConfigureAwait(false))!,
                subject,
                await _authorizations.GetApplicationIdAsync(authorization, cancellationToken).ConfigureAwait(false),
                scopes.Contains(McpScopes.Write, StringComparer.Ordinal),
                created!.Value));
        }

        var metas = await _meta.ListAsync(live.Select(l => l.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var apps = new Dictionary<string, (string? ClientId, string Name)>(StringComparer.Ordinal);
        var result = new List<OAuthConnection>(live.Count);
        foreach (var l in live)
        {
            var app = await AppAsync(l.ApplicationId, apps, cancellationToken).ConfigureAwait(false);
            metas.TryGetValue(l.Id, out var meta);
            result.Add(new OAuthConnection(
                l.Id, l.User, app.ClientId, app.Name, meta?.Label, l.CanWrite,
                l.Created, l.Created + OAuthGrants.MaxAge, meta?.LastUsedAt));
        }
        return result.OrderByDescending(c => c.ConnectedAt).ToList();
    }

    /// <summary>
    /// Name a connection; a blank name clears it, falling back to the client's own. False when
    /// there is no live authorization with this id for <paramref name="owner"/> (or for anyone,
    /// when null).
    /// </summary>
    public async Task<bool> RenameAsync(Guid? owner, string id, string? name, CancellationToken cancellationToken)
    {
        var found = await FindLiveAsync(owner, id, cancellationToken).ConfigureAwait(false);
        if (found is null)
            return false;
        var label = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        return await _meta.SetLabelAsync(id, found.Value.Owner, label, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// End a connection and every token issued under it. Returns the owner's id, so the caller
    /// can record whose access was ended, or null when there is no live authorization with
    /// this id for <paramref name="owner"/> (or for anyone, when null).
    /// </summary>
    public async Task<Guid?> RevokeAsync(Guid? owner, string id, CancellationToken cancellationToken)
    {
        var found = await FindLiveAsync(owner, id, cancellationToken).ConfigureAwait(false);
        if (found is null)
            return null;
        await OAuthGrants.RevokeAsync(_authorizations, _tokens, found.Value.Authorization, cancellationToken)
            .ConfigureAwait(false);
        return found.Value.Owner;
    }

    private async Task<(object Authorization, Guid Owner)?> FindLiveAsync(
        Guid? owner, string id, CancellationToken cancellationToken)
    {
        var authorization = await _authorizations.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (authorization is null
            || !await _authorizations.HasStatusAsync(authorization, Statuses.Valid, cancellationToken).ConfigureAwait(false)
            || !Guid.TryParse(
                await _authorizations.GetSubjectAsync(authorization, cancellationToken).ConfigureAwait(false),
                out var subject)
            || (owner is { } expected && expected != subject))
            return null;
        return (authorization, subject);
    }

    private async Task<(string? ClientId, string Name)> AppAsync(
        string? applicationId, Dictionary<string, (string? ClientId, string Name)> cache,
        CancellationToken cancellationToken)
    {
        if (applicationId is null)
            return (null, "Removed app");
        if (cache.TryGetValue(applicationId, out var known))
            return known;

        var application = await _applications.FindByIdAsync(applicationId, cancellationToken).ConfigureAwait(false);
        (string? ClientId, string Name) app;
        if (application is null)
        {
            app = (null, "Removed app");
        }
        else
        {
            var clientId = await _applications.GetClientIdAsync(application, cancellationToken).ConfigureAwait(false);
            var name = await _applications.GetDisplayNameAsync(application, cancellationToken).ConfigureAwait(false)
                       ?? clientId
                       ?? "Unnamed app";
            app = (clientId, name);
        }
        cache[applicationId] = app;
        return app;
    }
}
