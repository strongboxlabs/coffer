using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

using Coffer.Api.Auth;
using Coffer.Api.Configuration;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Mcp;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Admin-only deployment-wide settings surface (ADR-0063 §D8). Always mapped —
/// independent of whether MCP is currently enabled — so an admin can turn MCP
/// <em>on</em> from the UI while it is off. Every route is gated by
/// <see cref="AuthPolicies.RequireAdmin"/> (deployment-global capability, not
/// per-ledger); UI gating is UX, this is the boundary.
///
///   * GET /api/admin/system-settings/mcp — read the MCP toggle (+ live state)
///   * PUT /api/admin/system-settings/mcp — set it (takes effect on restart)
/// </summary>
public static class AdminSystemSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAdminSystemSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin/system-settings")
                          .RequireAuthorization(AuthPolicies.RequireAdmin);

        group.MapGet("/mcp", GetMcpAsync);
        group.MapPut("/mcp", SetMcpAsync);
        return routes;
    }

    /// <summary>
    /// The address to show an operator for an MCP client: the configured
    /// <c>Api:Mcp:PublicUrl</c>, else the origin this request arrived on.
    /// </summary>
    /// <remarks>
    /// The fallback is deliberately last. Deriving it from the request is right
    /// for a single-host install and wrong for a split one — the admin UI is
    /// browsed on the web host, so a request-derived answer would hand out the web
    /// address for a server that answers on its own hostname, and it would look
    /// plausible while being unusable. Configuring <c>COFFER_MCP_URL</c> is what
    /// makes it correct; the fallback just avoids showing nothing.
    /// </remarks>
    private static string ResolveMcpPublicUrl(ApiOptions options, HttpContext http)
    {
        var configured = options.Mcp.PublicUrl;
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');

        var request = http.Request;
        return request.Host.HasValue
            ? $"{request.Scheme}://{request.Host}"
            : string.Empty;
    }

    private static async Task<IResult> GetMcpAsync(
        SystemSettingsRepository settings,
        McpRuntimeState runtime,
        IOptions<ApiOptions> apiOptions,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var enabled = await settings
            .GetBoolAsync(SystemSettingsRepository.McpEnabledKey, fallback: false, cancellationToken)
            .ConfigureAwait(false);
        var writesEnabled = await settings
            .GetBoolAsync(SystemSettingsRepository.McpWritesEnabledKey, fallback: false, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(new McpSettingResponse(
            Enabled: enabled,
            Active: runtime.Active,
            ConfigForced: apiOptions.Value.Mcp.Enabled,
            WritesEnabled: writesEnabled,
            WritesActive: runtime.WritesEnabled,
            WritesConfigForced: apiOptions.Value.Mcp.WritesEnabled,
            PublicUrl: ResolveMcpPublicUrl(apiOptions.Value, http)));
    }

    private static async Task<IResult> SetMcpAsync(
        McpSettingRequest request,
        SystemSettingsRepository settings,
        ICurrentUserAccessor currentUser,
        McpRuntimeState runtime,
        IOptions<ApiOptions> apiOptions,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTime.UtcNow;
        await settings
            .SetBoolAsync(
                SystemSettingsRepository.McpEnabledKey,
                request.Enabled,
                currentUser.UserId,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        await settings
            .SetBoolAsync(
                SystemSettingsRepository.McpWritesEnabledKey,
                request.WritesEnabled,
                currentUser.UserId,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        // ADR-0081 D2: flip the HOT runtime flag so the kill-switch takes effect
        // immediately — McpWriteGuard reads this per call, no restart. (The master
        // mcp.enabled switch keeps restart semantics; Active is fixed for the
        // process's life, so turning MCP fully on/off still needs a restart.)
        runtime.WritesEnabled = request.WritesEnabled;

        return Results.Ok(new McpSettingResponse(
            Enabled: request.Enabled,
            Active: runtime.Active,
            ConfigForced: apiOptions.Value.Mcp.Enabled,
            WritesEnabled: request.WritesEnabled,
            WritesActive: runtime.WritesEnabled,
            WritesConfigForced: apiOptions.Value.Mcp.WritesEnabled,
            PublicUrl: ResolveMcpPublicUrl(apiOptions.Value, http)));
    }
}
