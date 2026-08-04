using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using OpenIddict.Abstractions;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Admin oversight of the MCP write surface (ADR-0081 D5): the OAuth/DCR clients that
/// can reach <c>/mcp</c> and the D3 write-audit log. Every route is
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

        // OAuth client management (ADR-0081 D5). No per-client write grant — writes are
        // gated by the kill-switch + guard, not the client's OAuth scope permission.
        group.MapGet("/clients", ListClientsAsync);
        group.MapDelete("/clients/{clientId}", RevokeClientAsync);
        group.MapPost("/clients/prune", PruneClientsAsync);

        return routes;
    }

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
        CancellationToken cancellationToken)
    {
        // Materialize the application list BEFORE the per-client authorization queries:
        // Npgsql has no MARS, so issuing a fresh query while ListAsync's reader is still
        // open throws "a command is already in progress". Get*Async read the already-loaded
        // entity (no query); FindByApplicationIdAsync is a new query, so it must run after
        // the list reader has closed.
        var apps = new List<object>();
        await foreach (var app in applications.ListAsync((int?)null, (int?)null, cancellationToken).ConfigureAwait(false))
            apps.Add(app);

        var clients = new List<McpClientDto>();
        foreach (var app in apps)
        {
            var id = await applications.GetIdAsync(app, cancellationToken).ConfigureAwait(false);
            var clientId = await applications.GetClientIdAsync(app, cancellationToken).ConfigureAwait(false);
            var name = await applications.GetDisplayNameAsync(app, cancellationToken).ConfigureAwait(false);
            var type = await applications.GetClientTypeAsync(app, cancellationToken).ConfigureAwait(false);
            var redirects = (await applications.GetRedirectUrisAsync(app, cancellationToken).ConfigureAwait(false))
                .Select(u => u.ToString()).ToList();

            // "Active authorizations" = a consent exists → the client is in use (so
            // prune leaves it alone). Count via the authorization store.
            var authorizationCount = 0;
            if (id is not null)
            {
                await foreach (var _ in authorizations.FindByApplicationIdAsync(id, cancellationToken).ConfigureAwait(false))
                    authorizationCount++;
            }

            clients.Add(new McpClientDto(
                clientId ?? string.Empty,
                name ?? clientId ?? "(unnamed)",
                type ?? "public",
                redirects,
                authorizationCount));
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

        var id = await applications.GetIdAsync(app, cancellationToken).ConfigureAwait(false);
        if (id is not null)
        {
            // Kill live access first — the client's tokens + authorizations — then the app.
            // Materialize each set before deleting (Npgsql has no MARS: no DELETE while the
            // FindByApplicationId reader is open).
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
        return Results.NoContent();
    }

    private static async Task<IResult> PruneClientsAsync(
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictTokenManager tokens,
        CancellationToken cancellationToken)
    {
        // Materialize the application list before the per-client authorization queries
        // (Npgsql has no MARS — no nested query while ListAsync's reader is open).
        var allApps = new List<object>();
        await foreach (var app in applications.ListAsync((int?)null, (int?)null, cancellationToken).ConfigureAwait(false))
            allApps.Add(app);

        // Collect clients with NO authorizations (never consented).
        var unused = new List<object>();
        foreach (var app in allApps)
        {
            var id = await applications.GetIdAsync(app, cancellationToken).ConfigureAwait(false);
            if (id is null) continue;

            var hasAuthorization = false;
            await foreach (var _ in authorizations.FindByApplicationIdAsync(id, cancellationToken).ConfigureAwait(false))
            {
                hasAuthorization = true;
                break;
            }
            if (!hasAuthorization) unused.Add(app);
        }

        foreach (var app in unused)
        {
            var id = await applications.GetIdAsync(app, cancellationToken).ConfigureAwait(false);
            if (id is not null)
            {
                var appTokens = new List<object>();
                await foreach (var token in tokens.FindByApplicationIdAsync(id, cancellationToken).ConfigureAwait(false))
                    appTokens.Add(token);
                foreach (var token in appTokens)
                    await tokens.DeleteAsync(token, cancellationToken).ConfigureAwait(false);
            }
            await applications.DeleteAsync(app, cancellationToken).ConfigureAwait(false);
        }
        return Results.Ok(new { pruned = unused.Count });
    }
}
