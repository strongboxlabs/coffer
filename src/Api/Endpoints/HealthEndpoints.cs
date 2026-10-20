using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Operational-probe endpoints. Both are anonymous and intentionally don't
/// run inside the authentication / authorisation pipeline so a probe never
/// fails for unrelated reasons (the orchestrator's complaint should be about
/// the API, not about the auth layer).
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// Liveness — the process is up. No DB I/O. Used by orchestrators
    /// (Docker, Compose, k8s) to decide whether to restart the container.
    /// </summary>
    public const string LivenessPath = "/healthz";

    /// <summary>
    /// Readiness — the process is up <em>and</em> can reach Postgres. Used
    /// to gate traffic; failing readiness means "alive but not serving."
    /// </summary>
    public const string ReadinessPath = "/readyz";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(LivenessPath, () => Results.Ok(new { status = "alive" }))
              .AllowAnonymous()
              .ExcludeFromDescription();

        routes.MapGet(ReadinessPath, async (
            AppDbContext db,
            CancellationToken cancellationToken) =>
            {
                // CanConnectAsync opens a connection and runs a minimal
                // probe query (the Npgsql provider sends "SELECT 1").
                // Returns true on success; transport failures throw and
                // bubble to a 5xx via the ProblemDetails handler.
                var ready = await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
                return ready
                    ? Results.Ok(new { status = "ready" })
                    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            })
              .AllowAnonymous()
              .ExcludeFromDescription();

        return routes;
    }
}
