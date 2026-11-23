using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using OpenIddict.Abstractions;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Mcp;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Admin oversight of MCP (ADR-0081 D5): who holds a connection to <c>/mcp</c>, the OAuth/DCR
/// clients registered against it, and the ADR-0081 D3 write-audit log. Every route is
/// <see cref="AuthPolicies.RequireAdmin"/>
/// (deployment-global capability); UI gating is UX, this is the boundary. Mapped only
/// when MCP is enabled (the OpenIddict managers exist only then).
/// </summary>
public static class AdminMcpEndpoints
{
    public static IEndpointRouteBuilder MapAdminMcpEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/mcp")
                          .RequireAuthorization(AuthPolicies.RequireAdmin);

        // Write-audit log (ADR-0081 D3).
        group.MapGet("/audit", ListAuditAsync);
        group.MapDelete("/audit", ClearAuditAsync);

        // Connections: one row per personal token or OAuth sign-in, with its owner and when it
        // was last used. A client registration is shared by everyone who connects through it,
        // so it cannot say who is using it; a connection can.
        group.MapGet("/connections", ListConnectionsAsync);
        group.MapPatch("/connections/{kind}/{id}", RenameConnectionAsync);
        group.MapDelete("/connections/{kind}/{id}", RevokeConnectionAsync);

        // OAuth client registrations (ADR-0081 D5, ADR-0113): the apps the connections above are
        // grouped under. Revoke one (ending every connection through it) or prune the ones with
        // no live connection. No per-client write grant — writes are gated by the kill-switch +
        // guard, not the client's OAuth scope permission.
        group.MapGet("/clients", ListClientsAsync);
        group.MapDelete("/clients/{clientId}", RevokeClientAsync);
        group.MapPost("/clients/prune", PruneClientsAsync);

        return routes;
    }

    private static async Task<IResult> ListConnectionsAsync(
        McpTokensRepository tokens,
        OAuthConnectionsService oauth,
        UsersRepository users,
        CancellationToken cancellationToken)
    {
        var tokenRows = await tokens.ListAllLiveAsync(cancellationToken).ConfigureAwait(false);
        var oauthRows = await oauth.ListAsync(owner: null, cancellationToken).ConfigureAwait(false);
        var names = await users.DisplayNamesAsync(
            oauthRows.Select(c => c.UserId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);

        var connections = new List<McpConnectionDto>(tokenRows.Count + oauthRows.Count);
        connections.AddRange(tokenRows.Select(t => new McpConnectionDto(
            McpConnectionKinds.Token, t.Id.ToString(), t.UserId, t.User,
            Name: t.Name, Label: null, AppName: null, ClientId: null,
            CanWrite: t.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(McpScopes.Write),
            t.CreatedAt, t.LastUsedAt, t.ExpiresAt)));
        connections.AddRange(oauthRows.Select(c => new McpConnectionDto(
            McpConnectionKinds.OAuth, c.Id, c.UserId, names.GetValueOrDefault(c.UserId, "Unknown user"),
            Name: c.Label ?? c.AppName, Label: c.Label, AppName: c.AppName, ClientId: c.ClientId,
            c.CanWrite, c.ConnectedAt.UtcDateTime, c.LastUsedAt, c.ExpiresAt.UtcDateTime)));
        return Results.Ok(connections.OrderByDescending(c => c.ConnectedAt).ToList());
    }

    private static async Task<IResult> RenameConnectionAsync(
        string kind,
        string id,
        RenameConnectionRequest request,
        McpTokensRepository tokens,
        OAuthConnectionsService oauth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool renamed;
        switch (kind)
        {
            case McpConnectionKinds.Token:
                var name = request.Name?.Trim();
                if (string.IsNullOrEmpty(name))
                    return BusinessError.Problem(BusinessError.Codes.McpTokenNameRequired,
                        "A name for the connected app is required.");
                renamed = Guid.TryParse(id, out var tokenId)
                    && await tokens.RenameAnyAsync(tokenId, name, cancellationToken).ConfigureAwait(false);
                break;
            case McpConnectionKinds.OAuth:
                renamed = await oauth.RenameAsync(owner: null, id, request.Name, cancellationToken).ConfigureAwait(false);
                break;
            default:
                renamed = false;
                break;
        }
        return renamed ? Results.NoContent() : ConnectionNotFound();
    }

    private static async Task<IResult> RevokeConnectionAsync(
        string kind,
        string id,
        McpTokensRepository tokens,
        OAuthConnectionsService oauth,
        AdminAuditRepository audit,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        Guid? owner = kind switch
        {
            McpConnectionKinds.Token => Guid.TryParse(id, out var tokenId)
                ? await tokens.RevokeAnyAsync(tokenId, cancellationToken).ConfigureAwait(false)
                : null,
            McpConnectionKinds.OAuth => await oauth.RevokeAsync(owner: null, id, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
        if (owner is null)
            return ConnectionNotFound();

        // Ending someone else's access is an admin action on another person; it is recorded
        // like the other privilege changes (FU-090). The detail names the kind and the owner,
        // never the token.
        await audit.AppendAsync(
            AdminAuditActions.McpConnectionRevoked, currentUser.UserId,
            $"{kind} of user {owner}", cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static IResult ConnectionNotFound() =>
        BusinessError.Problem(BusinessError.Codes.McpConnectionNotFound, "No such connection.");

    private static async Task<IResult> ListAuditAsync(
        McpAuditRepository audit,
        int? take,
        DateTime? before,
        CancellationToken cancellationToken)
    {
        var entries = await audit.ListAsync(take ?? 100, before, cancellationToken).ConfigureAwait(false);
        return Results.Ok(entries);
    }

    private static async Task<IResult> ClearAuditAsync(
        McpAuditRepository audit,
        DateTime? before,
        CancellationToken cancellationToken)
    {
        var deleted = await audit.ClearAsync(before, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { deleted });
    }

    private static async Task<IResult> ListClientsAsync(
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var clients = new List<McpClientDto>();
        foreach (var app in await ListApplicationsAsync(applications, cancellationToken).ConfigureAwait(false))
        {
            var clientId = await applications.GetClientIdAsync(app, cancellationToken).ConfigureAwait(false);
            var name = await applications.GetDisplayNameAsync(app, cancellationToken).ConfigureAwait(false);
            var type = await applications.GetClientTypeAsync(app, cancellationToken).ConfigureAwait(false);
            var redirects = (await applications.GetRedirectUrisAsync(app, cancellationToken).ConfigureAwait(false))
                .Select(u => u.ToString()).ToList();

            clients.Add(new McpClientDto(
                clientId ?? string.Empty,
                name ?? clientId ?? "(unnamed)",
                type ?? "public",
                redirects,
                await LiveConnectionCountAsync(applications, authorizations, app, now, cancellationToken).ConfigureAwait(false),
                await OAuthClientRegistration.ReadRegisteredAtAsync(applications, app, cancellationToken).ConfigureAwait(false)));
        }
        return Results.Ok(clients);
    }

    private static async Task<IResult> RevokeClientAsync(
        string clientId,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictTokenManager tokens,
        CancellationToken cancellationToken)
    {
        var app = await applications.FindByClientIdAsync(clientId, cancellationToken).ConfigureAwait(false);
        if (app is null) return Results.NotFound();

        await DeleteClientAsync(applications, authorizations, tokens, app, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> PruneClientsAsync(
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictTokenManager tokens,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // A client with no live connection is unused, whether nobody ever signed in through it
        // or every sign-in through it was revoked or ran past its age: none of those can do
        // anything, and nothing else removes their records, so without this a client used
        // once stays registered for ever and counts against the registration cap. An app that
        // still holds the deleted registration registers again on its next connect.
        var now = clock.GetUtcNow();
        var unused = new List<object>();
        foreach (var app in await ListApplicationsAsync(applications, cancellationToken).ConfigureAwait(false))
        {
            if (await LiveConnectionCountAsync(applications, authorizations, app, now, cancellationToken).ConfigureAwait(false) == 0)
                unused.Add(app);
        }

        foreach (var app in unused)
            await DeleteClientAsync(applications, authorizations, tokens, app, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { pruned = unused.Count });
    }

    /// <summary>
    /// Every registered application, read in full before any per-application query: Npgsql has
    /// no MARS, so a query issued while the listing's reader is still open throws "a command is
    /// already in progress".
    /// </summary>
    private static async Task<List<object>> ListApplicationsAsync(
        IOpenIddictApplicationManager applications, CancellationToken cancellationToken)
    {
        var apps = new List<object>();
        await foreach (var app in applications.ListAsync((int?)null, (int?)null, cancellationToken).ConfigureAwait(false))
            apps.Add(app);
        return apps;
    }

    /// <summary>
    /// How many live connections were made through <paramref name="app"/>, by
    /// <see cref="OAuthGrants.IsLiveAsync"/>: the rule the connection list shows by, so a
    /// client's count and the sign-ins listed under it agree.
    /// </summary>
    private static async Task<int> LiveConnectionCountAsync(
        IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations,
        object app, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var id = await applications.GetIdAsync(app, cancellationToken).ConfigureAwait(false);
        if (id is null) return 0;

        // Read them all before asking about any (no MARS).
        var all = new List<object>();
        await foreach (var authorization in authorizations.FindByApplicationIdAsync(id, cancellationToken).ConfigureAwait(false))
            all.Add(authorization);

        var live = 0;
        foreach (var authorization in all)
        {
            if (await OAuthGrants.IsLiveAsync(authorizations, authorization, now, cancellationToken).ConfigureAwait(false))
                live++;
        }
        return live;
    }

    /// <summary>
    /// Delete a registration and everything issued under it: its tokens, then its
    /// authorizations (live or not; each takes its connection record with it, ADR-0107 D3),
    /// then the application. Each set is read in full before any delete (no MARS).
    /// </summary>
    private static async Task DeleteClientAsync(
        IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations,
        IOpenIddictTokenManager tokens, object app, CancellationToken cancellationToken)
    {
        var id = await applications.GetIdAsync(app, cancellationToken).ConfigureAwait(false);
        if (id is not null)
        {
            var appTokens = new List<object>();
            await foreach (var token in tokens.FindByApplicationIdAsync(id, cancellationToken).ConfigureAwait(false))
                appTokens.Add(token);
            foreach (var token in appTokens)
                await tokens.DeleteAsync(token, cancellationToken).ConfigureAwait(false);

            var appAuthorizations = new List<object>();
            await foreach (var authorization in authorizations.FindByApplicationIdAsync(id, cancellationToken).ConfigureAwait(false))
                appAuthorizations.Add(authorization);
            foreach (var authorization in appAuthorizations)
                await authorizations.DeleteAsync(authorization, cancellationToken).ConfigureAwait(false);
        }
        await applications.DeleteAsync(app, cancellationToken).ConfigureAwait(false);
    }
}
