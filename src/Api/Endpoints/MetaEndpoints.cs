using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Meta;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Installation-wide metadata endpoints (ADR-0044). Authenticated —
/// unlike the anonymous <c>/healthz</c> / <c>/readyz</c> probes, the
/// version payload is for a logged-in user's About panel, so there's no
/// reason to disclose build identity / schema state to anonymous
/// callers.
/// </summary>
public static class MetaEndpoints
{
    public static IEndpointRouteBuilder MapMetaEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/meta")
                          .RequireAuthorization();

        group.MapGet("/version", GetVersionAsync);

        return routes;
    }

    /// <summary>
    /// <c>GET /api/meta/version</c> — the two server-side version axes
    /// (ADR-0044). The API axis comes from assembly attributes stamped
    /// at build; the DB axis is the latest applied migration. The SPA
    /// supplies its own (UI) axis from build-time constants.
    /// </summary>
    private static async Task<IResult> GetVersionAsync(
        MetaRepository meta,
        CancellationToken cancellationToken)
    {
        var script = await meta.GetLatestSchemaScriptAsync(cancellationToken)
            .ConfigureAwait(false);
        var (schemaVersion, scriptName) = ParseSchemaScript(script);

        var response = new VersionResponse(
            Api: new ApiVersionDto(
                VersionInfo.Semver,
                VersionInfo.Build,
                VersionInfo.Commit,
                VersionInfo.CommitDate),
            Db: new DbVersionDto(schemaVersion, scriptName));

        return Results.Ok(response);
    }

    /// <summary>
    /// Reduce a DbUp script name to its migration number + a clean
    /// display name. DbUp may record a bare filename or a path; we
    /// strip any directory and the <c>.sql</c> extension, then read the
    /// leading <c>NNN</c> as the schema version. Returns
    /// <c>(0, "")</c> for a null/empty input (no migrations recorded).
    /// </summary>
    private static (int Version, string Script) ParseSchemaScript(string? script)
    {
        if (string.IsNullOrWhiteSpace(script))
            return (0, string.Empty);

        var name = script;
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
            name = name[(slash + 1)..];
        if (name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        var digits = new string(name.TakeWhile(char.IsDigit).ToArray());
        _ = int.TryParse(digits, out var version);
        return (version, name);
    }
}
